using System.Diagnostics;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class ModelRouter : IModelRouter
{
    private readonly IBedrockService _bedrockService;
    private readonly ILocalModelService _localModelService;
    private readonly IApplicationRegistryService _registryService;
    private readonly IGuardrailService _guardrailService;
    private readonly GatewayOptions _options;
    private readonly ILogger<ModelRouter> _logger;

    public ModelRouter(
        IBedrockService bedrockService,
        ILocalModelService localModelService,
        IApplicationRegistryService registryService,
        IGuardrailService guardrailService,
        IOptions<GatewayOptions> options,
        ILogger<ModelRouter> logger)
    {
        _bedrockService = bedrockService;
        _localModelService = localModelService;
        _registryService = registryService;
        _guardrailService = guardrailService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UniversalResponse> RouteAppRequestAsync(string appId, InvokeAppRequest request, CancellationToken cancellationToken = default)
    {
        var app = await _registryService.GetAppAsync(appId, cancellationToken);
        if (app == null)
        {
            return new UniversalResponse
            {
                Output = string.Empty,
                AppId = appId,
                Error = new GatewayError
                {
                    Code = "APP_NOT_FOUND",
                    Message = $"Application '{appId}' is not registered."
                }
            };
        }

        if (!app.IsActive)
        {
            return new UniversalResponse
            {
                Output = string.Empty,
                AppId = appId,
                Error = new GatewayError
                {
                    Code = "APP_INACTIVE",
                    Message = $"Application '{appId}' is currently deactivated."
                }
            };
        }

        var universalReq = new UniversalRequest
        {
            Model = app.Model,
            Provider = app.Provider,
            Input = request.Input,
            System = app.SystemPrompt,
            Temperature = request.Temperature ?? app.Temperature,
            MaxTokens = request.MaxTokens ?? app.MaxTokens,
            Metadata = new RequestMetadata
            {
                AppId = app.AppId,
                UserId = request.UserId,
                SessionId = request.SessionId,
                Extra = request.Metadata
            }
        };

        return await RouteWithFallbackAsync(universalReq, app.FallbackProvider, app.FallbackModel, cancellationToken);
    }

    public async Task<UniversalResponse> RouteAsync(UniversalRequest request, CancellationToken cancellationToken = default)
    {
        return await RouteWithFallbackAsync(request, null, null, cancellationToken);
    }

    private async Task<UniversalResponse> RouteWithFallbackAsync(
        UniversalRequest request,
        string? fallbackProvider,
        string? fallbackModel,
        CancellationToken cancellationToken)
    {
        var overallStopwatch = Stopwatch.StartNew();

        // 1. Enterprise Guardrail Inspection before hitting any backend
        var guardrailResult = await _guardrailService.EvaluateAsync(
            request.Input,
            request.System,
            cancellationToken: cancellationToken);

        if (guardrailResult.IsBlocked)
        {
            overallStopwatch.Stop();
            _logger.LogWarning("Request blocked by enterprise guardrails for AppId: {AppId}. Violations: {Violations}",
                request.Metadata?.AppId ?? "direct",
                string.Join("; ", guardrailResult.Violations.Select(v => v.RuleName)));

            var blockedRes = new UniversalResponse
            {
                Output = string.Empty,
                Model = request.Model,
                Provider = request.Provider ?? "guardrail",
                LatencyMs = overallStopwatch.ElapsedMilliseconds,
                AppId = request.Metadata?.AppId,
                SessionId = request.Metadata?.SessionId,
                Error = new GatewayError
                {
                    Code = "GUARDRAIL_BLOCKED",
                    Message = "Request blocked by enterprise security guardrail policy.",
                    Details = string.Join("; ", guardrailResult.Violations.Select(v => $"{v.Category} ({v.RuleName}): {v.Description}"))
                }
            };

            await RecordTelemetryAsync(request, blockedRes, false, guardrailResult);
            return blockedRes;
        }

        // Apply sanitized/redacted input if redaction occurred
        var sanitizedReq = request;
        if (guardrailResult.ActionTaken == "Redacted")
        {
            _logger.LogInformation("Guardrails redacted sensitive data in input before routing to {Model}", request.Model);
            sanitizedReq = request with { Input = guardrailResult.SanitizedInput };
        }

        var primaryProvider = ResolveProvider(sanitizedReq);
        var primaryModel = sanitizedReq.Model;

        _logger.LogInformation("Routing request. Primary Provider: {Provider}, Primary Model: {Model}, AppId: {AppId}",
            primaryProvider, primaryModel, sanitizedReq.Metadata?.AppId ?? "direct");

        UniversalResponse response;

        try
        {
            response = await DispatchToProviderAsync(primaryProvider, sanitizedReq, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary provider '{Provider}' failed with exception for model '{Model}'", primaryProvider, primaryModel);
            response = new UniversalResponse
            {
                Output = string.Empty,
                Model = primaryModel,
                Provider = primaryProvider,
                Error = new GatewayError
                {
                    Code = "PRIMARY_PROVIDER_FAILED",
                    Message = ex.Message
                }
            };
        }

        // If primary succeeded, record and return
        if (response.Error == null && !string.IsNullOrEmpty(response.Output))
        {
            overallStopwatch.Stop();
            var finalRes = response with { LatencyMs = overallStopwatch.ElapsedMilliseconds };
            await RecordTelemetryAsync(sanitizedReq, finalRes, false, guardrailResult);
            return finalRes;
        }

        // Check if fallback is configured
        if (!string.IsNullOrWhiteSpace(fallbackProvider) || !string.IsNullOrWhiteSpace(fallbackModel))
        {
            var targetFallbackProvider = !string.IsNullOrWhiteSpace(fallbackProvider)
                ? fallbackProvider.ToLowerInvariant()
                : (primaryProvider == "bedrock" ? "local" : "bedrock");

            var targetFallbackModel = !string.IsNullOrWhiteSpace(fallbackModel)
                ? fallbackModel
                : (targetFallbackProvider == "local" ? "ollama/llama3" : "anthropic.claude-3-haiku-20240307-v1:0");

            _logger.LogWarning("Triggering automatic fallback. Primary failed ({ErrorCode}). Attempting Fallback Provider: {FallbackProvider}, Fallback Model: {FallbackModel}",
                response.Error?.Code ?? "NO_OUTPUT", targetFallbackProvider, targetFallbackModel);

            var fallbackReq = sanitizedReq with
            {
                Provider = targetFallbackProvider,
                Model = targetFallbackModel
            };

            try
            {
                var fallbackResponse = await DispatchToProviderAsync(targetFallbackProvider, fallbackReq, cancellationToken);
                overallStopwatch.Stop();

                if (fallbackResponse.Error == null)
                {
                    var finalFallback = fallbackResponse with
                    {
                        FallbackUsed = true,
                        LatencyMs = overallStopwatch.ElapsedMilliseconds
                    };

                    await RecordTelemetryAsync(sanitizedReq, finalFallback, true, guardrailResult);
                    return finalFallback;
                }

                _logger.LogError("Both primary and fallback providers failed for request.");
                overallStopwatch.Stop();
                var dualFailure = response with
                {
                    LatencyMs = overallStopwatch.ElapsedMilliseconds,
                    Error = new GatewayError
                    {
                        Code = "ALL_PROVIDERS_FAILED",
                        Message = $"Primary failed: [{response.Error?.Message}]. Fallback failed: [{fallbackResponse.Error?.Message}]"
                    }
                };
                await RecordTelemetryAsync(sanitizedReq, dualFailure, false, guardrailResult);
                return dualFailure;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback provider execution failed with unexpected exception.");
            }
        }

        overallStopwatch.Stop();
        var failureRes = response with { LatencyMs = overallStopwatch.ElapsedMilliseconds };
        await RecordTelemetryAsync(sanitizedReq, failureRes, false, guardrailResult);
        return failureRes;
    }

    private async Task<UniversalResponse> DispatchToProviderAsync(
        string provider,
        UniversalRequest request,
        CancellationToken cancellationToken)
    {
        return provider.ToLowerInvariant() switch
        {
            "bedrock" or "aws" => await _bedrockService.InvokeModelAsync(request, cancellationToken),
            "local" or "ollama" or "lmstudio" or "llamacpp" => await _localModelService.InvokeLocalModelAsync(request, cancellationToken),
            _ => await _bedrockService.InvokeModelAsync(request, cancellationToken)
        };
    }

    private static string ResolveProvider(UniversalRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Provider))
        {
            return req.Provider.ToLowerInvariant();
        }

        var model = req.Model.ToLowerInvariant();
        if (model.StartsWith("ollama/") || model.StartsWith("lmstudio/") || model.StartsWith("llamacpp/") || model.StartsWith("llama.cpp/"))
        {
            return "local";
        }

        if (model.Contains("claude") || model.Contains("titan") || model.Contains("meta.llama") || model.Contains("anthropic."))
        {
            return "bedrock";
        }

        return "bedrock";
    }

    private async Task RecordTelemetryAsync(
        UniversalRequest req,
        UniversalResponse res,
        bool fallbackUsed,
        GuardrailResult guardrailResult)
    {
        try
        {
            var log = new RequestLogEntry
            {
                AppId = req.Metadata?.AppId,
                Model = res.Model,
                Provider = res.Provider,
                LatencyMs = res.LatencyMs,
                InputTokens = res.Tokens.Input,
                OutputTokens = res.Tokens.Output,
                Success = res.Error == null,
                FallbackUsed = fallbackUsed,
                GuardrailAction = guardrailResult.ActionTaken,
                GuardrailViolations = guardrailResult.Violations.Select(v => $"{v.Category}:{v.RuleName}").ToList(),
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = res.Error?.Message
            };

            await _registryService.RecordMetricAsync(log);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record telemetry log");
        }
    }
}
