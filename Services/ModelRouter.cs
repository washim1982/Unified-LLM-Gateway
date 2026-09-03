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
    private readonly IAuditLogService _auditLogService;
    private readonly IPrometheusMetricsService _prometheusMetrics;
    private readonly GatewayOptions _options;
    private readonly ILogger<ModelRouter> _logger;

    public ModelRouter(
        IBedrockService bedrockService,
        ILocalModelService localModelService,
        IApplicationRegistryService registryService,
        IGuardrailService guardrailService,
        IAuditLogService auditLogService,
        IPrometheusMetricsService prometheusMetrics,
        IOptions<GatewayOptions> options,
        ILogger<ModelRouter> logger)
    {
        _bedrockService = bedrockService;
        _localModelService = localModelService;
        _registryService = registryService;
        _guardrailService = guardrailService;
        _auditLogService = auditLogService;
        _prometheusMetrics = prometheusMetrics;
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

        // 1. Inbound Enterprise Guardrail Inspection before hitting any backend
        var inputGuardrailResult = await _guardrailService.EvaluateAsync(
            request.Input,
            request.System,
            cancellationToken: cancellationToken);

        if (inputGuardrailResult.IsBlocked)
        {
            overallStopwatch.Stop();
            _logger.LogWarning("Request blocked by inbound guardrails for AppId: {AppId}. Violations: {Violations}",
                request.Metadata?.AppId ?? "direct",
                string.Join("; ", inputGuardrailResult.Violations.Select(v => v.RuleName)));

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
                    Details = string.Join("; ", inputGuardrailResult.Violations.Select(v => $"{v.Category} ({v.RuleName}): {v.Description}"))
                }
            };

            await RecordTelemetryAndAuditAsync(request, blockedRes, false, inputGuardrailResult, null);
            return blockedRes;
        }

        // Apply sanitized/redacted input if input redaction occurred
        var sanitizedReq = request;
        if (inputGuardrailResult.ActionTaken == "Redacted")
        {
            _logger.LogInformation("Guardrails redacted sensitive data in prompt before routing to {Model}", request.Model);
            sanitizedReq = request with { Input = inputGuardrailResult.SanitizedInput };
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

        // 2. Primary Provider Success -> Perform Output Guardrail Inspection
        if (response.Error == null && !string.IsNullOrEmpty(response.Output))
        {
            var outputGuardrailResult = await _guardrailService.EvaluateOutputAsync(response.Output, cancellationToken: cancellationToken);

            if (outputGuardrailResult.IsBlocked)
            {
                overallStopwatch.Stop();
                _logger.LogWarning("Model output blocked by output guardrails for AppId: {AppId}. Leaked items: {Violations}",
                    sanitizedReq.Metadata?.AppId ?? "direct",
                    string.Join("; ", outputGuardrailResult.Violations.Select(v => v.RuleName)));

                var blockedOutputRes = new UniversalResponse
                {
                    Output = string.Empty,
                    Model = primaryModel,
                    Provider = primaryProvider,
                    LatencyMs = overallStopwatch.ElapsedMilliseconds,
                    AppId = sanitizedReq.Metadata?.AppId,
                    SessionId = sanitizedReq.Metadata?.SessionId,
                    Error = new GatewayError
                    {
                        Code = "GUARDRAIL_OUTPUT_BLOCKED",
                        Message = "Model response suppressed due to sensitive data leakage policy violation.",
                        Details = string.Join("; ", outputGuardrailResult.Violations.Select(v => $"{v.Category} ({v.RuleName}): {v.Description}"))
                    }
                };

                await RecordTelemetryAndAuditAsync(sanitizedReq, blockedOutputRes, false, inputGuardrailResult, outputGuardrailResult);
                return blockedOutputRes;
            }

            overallStopwatch.Stop();
            var sanitizedOutput = outputGuardrailResult.ActionTaken == "Redacted"
                ? outputGuardrailResult.SanitizedInput
                : response.Output;

            var finalRes = response with
            {
                Output = sanitizedOutput,
                LatencyMs = overallStopwatch.ElapsedMilliseconds
            };

            await RecordTelemetryAndAuditAsync(sanitizedReq, finalRes, false, inputGuardrailResult, outputGuardrailResult);
            return finalRes;
        }

        // 3. Trigger Fallback if Primary Failed
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
                    var fallbackOutputGuardrail = await _guardrailService.EvaluateOutputAsync(fallbackResponse.Output, cancellationToken: cancellationToken);

                    if (fallbackOutputGuardrail.IsBlocked)
                    {
                        var blockedFallbackRes = new UniversalResponse
                        {
                            Output = string.Empty,
                            Model = targetFallbackModel,
                            Provider = targetFallbackProvider,
                            FallbackUsed = true,
                            LatencyMs = overallStopwatch.ElapsedMilliseconds,
                            AppId = sanitizedReq.Metadata?.AppId,
                            SessionId = sanitizedReq.Metadata?.SessionId,
                            Error = new GatewayError
                            {
                                Code = "GUARDRAIL_OUTPUT_BLOCKED",
                                Message = "Fallback response suppressed due to sensitive data leakage policy violation.",
                                Details = string.Join("; ", fallbackOutputGuardrail.Violations.Select(v => $"{v.Category} ({v.RuleName}): {v.Description}"))
                            }
                        };
                        await RecordTelemetryAndAuditAsync(sanitizedReq, blockedFallbackRes, true, inputGuardrailResult, fallbackOutputGuardrail);
                        return blockedFallbackRes;
                    }

                    var sanitizedFallbackOutput = fallbackOutputGuardrail.ActionTaken == "Redacted"
                        ? fallbackOutputGuardrail.SanitizedInput
                        : fallbackResponse.Output;

                    var finalFallback = fallbackResponse with
                    {
                        Output = sanitizedFallbackOutput,
                        FallbackUsed = true,
                        LatencyMs = overallStopwatch.ElapsedMilliseconds
                    };

                    await RecordTelemetryAndAuditAsync(sanitizedReq, finalFallback, true, inputGuardrailResult, fallbackOutputGuardrail);
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
                await RecordTelemetryAndAuditAsync(sanitizedReq, dualFailure, false, inputGuardrailResult, null);
                return dualFailure;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback provider execution failed with unexpected exception.");
            }
        }

        overallStopwatch.Stop();
        var failureRes = response with { LatencyMs = overallStopwatch.ElapsedMilliseconds };
        await RecordTelemetryAndAuditAsync(sanitizedReq, failureRes, false, inputGuardrailResult, null);
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

    private async Task RecordTelemetryAndAuditAsync(
        UniversalRequest req,
        UniversalResponse res,
        bool fallbackUsed,
        GuardrailResult inputGuardrail,
        GuardrailResult? outputGuardrail)
    {
        try
        {
            var allViolations = new List<string>();
            if (inputGuardrail.Violations.Count > 0)
            {
                allViolations.AddRange(inputGuardrail.Violations.Select(v => $"Input:{v.Category}:{v.RuleName}"));
            }
            if (outputGuardrail != null && outputGuardrail.Violations.Count > 0)
            {
                allViolations.AddRange(outputGuardrail.Violations.Select(v => $"Output:{v.Category}:{v.RuleName}"));
            }

            var overallGuardrailAction = inputGuardrail.ActionTaken != "Passed"
                ? inputGuardrail.ActionTaken
                : (outputGuardrail?.ActionTaken ?? "Passed");

            // 1. In-memory dashboard metrics
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
                GuardrailAction = overallGuardrailAction,
                GuardrailViolations = allViolations,
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = res.Error?.Message
            };

            await _registryService.RecordMetricAsync(log);

            // 2. Persistent Disk Audit Trail
            var auditRecord = new AuditLogRecord
            {
                AuditId = log.Id,
                Timestamp = log.Timestamp,
                AppId = req.Metadata?.AppId,
                CallerId = req.Metadata?.UserId ?? req.Metadata?.SessionId,
                AuthType = req.Metadata?.AppId != null ? "Application" : "Direct",
                Route = req.Metadata?.AppId != null ? $"/gateway/{req.Metadata.AppId}/invoke" : "/gateway/universal/invoke",
                Model = res.Model,
                Provider = res.Provider,
                InputGuardrailAction = inputGuardrail.ActionTaken,
                OutputGuardrailAction = outputGuardrail?.ActionTaken ?? "None",
                GuardrailViolations = allViolations,
                InputTokens = res.Tokens.Input,
                OutputTokens = res.Tokens.Output,
                LatencyMs = res.LatencyMs,
                StatusCode = res.Error == null ? 200 : (res.Error.Code.Contains("BLOCKED") ? 422 : 500),
                Success = res.Error == null,
                FallbackUsed = fallbackUsed,
                ErrorMessage = res.Error?.Message
            };

            _auditLogService.LogRequest(auditRecord);

            // 3. Prometheus Metrics Recording
            if (_options.Observability.EnablePrometheus)
            {
                var appId = req.Metadata?.AppId ?? "direct";
                _prometheusMetrics.RecordRequest(
                    appId,
                    res.Model,
                    res.Provider,
                    auditRecord.StatusCode,
                    res.LatencyMs / 1000.0);

                _prometheusMetrics.RecordTokens(
                    appId,
                    res.Model,
                    res.Tokens.Input,
                    res.Tokens.Output);

                if (inputGuardrail.Violations.Count > 0)
                {
                    foreach (var v in inputGuardrail.Violations)
                    {
                        _prometheusMetrics.RecordGuardrailViolation(v.Category, inputGuardrail.ActionTaken, v.Severity);
                    }
                }

                if (outputGuardrail != null && outputGuardrail.Violations.Count > 0)
                {
                    foreach (var v in outputGuardrail.Violations)
                    {
                        _prometheusMetrics.RecordGuardrailViolation(v.Category, outputGuardrail.ActionTaken, v.Severity);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record telemetry and audit trail");
        }
    }
}
