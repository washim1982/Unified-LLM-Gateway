using Amazon;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;
using UnifiedGateway.Services;

namespace UnifiedGateway.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Dashboard & Management");

        #region Application Management Endpoints

        // List applications
        group.MapGet("/apps", async (IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var apps = await registry.GetAllAppsAsync(ct);
            return Results.Ok(apps);
        })
        .WithName("ListApplications");

        // Get single application
        group.MapGet("/apps/{appId}", async (string appId, IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var appConfig = await registry.GetAppAsync(appId, ct);
            return appConfig is not null ? Results.Ok(appConfig) : Results.NotFound(new { error = "Application not found" });
        })
        .WithName("GetApplication");

        // Create new application (generates endpoint, API key, and initial STS token)
        group.MapPost("/apps", async ([FromBody] CreateAppRequest request, IApplicationRegistryService registry, CancellationToken ct) =>
        {
            try
            {
                var created = await registry.CreateAppAsync(request, ct);
                return Results.Created($"/api/apps/{created.App.AppId}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateApplication");

        // Update application (increments version and updates prompt/config)
        group.MapPut("/apps/{appId}", async (string appId, [FromBody] UpdateAppRequest request, IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var updated = await registry.UpdateAppAsync(appId, request, ct);
            return updated is not null ? Results.Ok(updated) : Results.NotFound(new { error = "Application not found" });
        })
        .WithName("UpdateApplication");

        // Delete application
        group.MapDelete("/apps/{appId}", async (string appId, IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var deleted = await registry.DeleteAppAsync(appId, ct);
            return deleted ? Results.NoContent() : Results.NotFound(new { error = "Application not found" });
        })
        .WithName("DeleteApplication");

        // Mint short temporary secret (STS token) for an application from dashboard
        group.MapPost("/apps/{appId}/sts-token", async (
            string appId,
            [FromQuery] int? durationSeconds,
            [FromQuery] string? scope,
            IApplicationRegistryService registry,
            CancellationToken ct) =>
        {
            var app = await registry.GetAppAsync(appId, ct);
            if (app == null)
            {
                return Results.NotFound(new { error = "Application not found" });
            }

            var tokenResp = await registry.MintStsTokenDirectAsync(
                appId,
                durationSeconds ?? 3600,
                scope ?? "invoke",
                isAdmin: false,
                callerId: "DashboardUser",
                cancellationToken: ct);

            return Results.Ok(tokenResp);
        })
        .WithName("MintAppStsToken");

        // Rotate Application API Key (Zero-Downtime with Grace Period)
        group.MapPost("/apps/{appId}/rotate-key", async (
            string appId,
            [FromBody] RotateKeyRequest? request,
            IApplicationRegistryService registry,
            CancellationToken ct) =>
        {
            var graceDays = request?.GracePeriodDays ?? 7;
            var rotateResp = await registry.RotateAppApiKeyAsync(appId, graceDays, ct);

            if (rotateResp == null)
            {
                return Results.NotFound(new { error = "Application not found" });
            }

            return Results.Ok(rotateResp);
        })
        .WithName("RotateAppApiKey")
        .WithSummary("Rotate primary application API key while keeping previous key as secondary grace-period key");

        // Revoke Secondary Application API Key (Emergency Revocation)
        group.MapPost("/apps/{appId}/revoke-secondary-key", async (
            string appId,
            IApplicationRegistryService registry,
            CancellationToken ct) =>
        {
            var revokeResp = await registry.RevokeSecondaryApiKeyAsync(appId, ct);

            if (revokeResp == null)
            {
                return Results.NotFound(new { error = "Application not found" });
            }

            return Results.Ok(revokeResp);
        })
        .WithName("RevokeSecondaryApiKey")
        .WithSummary("Immediately revoke secondary grace-period key for an application");

        // Test application invocation from dashboard
        group.MapPost("/apps/{appId}/test", async (
            string appId,
            [FromBody] InvokeAppRequest request,
            IModelRouter router,
            CancellationToken ct) =>
        {
            var result = await router.RouteAppRequestAsync(appId, request, ct);
            return Results.Ok(result);
        })
        .WithName("TestApplication");

        #endregion

        #region Telemetry, Analytics & Audit Endpoints

        // Prometheus Standard Text Scraper Endpoint (/metrics)
        app.MapGet("/metrics", (IPrometheusMetricsService metricsService) =>
        {
            var text = metricsService.GeneratePrometheusMetricsText();
            return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
        })
        .WithName("PrometheusMetrics")
        .WithTags("Observability & Prometheus")
        .WithSummary("Standard Prometheus text exposition scrape endpoint (version 0.0.4)");

        // Real-time JSON metrics and analytics summary for Web Dashboard
        group.MapGet("/metrics", async (IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var summary = await registry.GetMetricsSummaryAsync(ct);
            return Results.Ok(summary);
        })
        .WithName("GetMetrics");

        // Alias for Prometheus text metrics under /api/metrics/prometheus
        group.MapGet("/metrics/prometheus", (IPrometheusMetricsService metricsService) =>
        {
            var text = metricsService.GeneratePrometheusMetricsText();
            return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
        })
        .WithName("GetPrometheusMetricsText");

        // Query persistent compliance audit logs
        group.MapGet("/audit/logs", async (
            [FromQuery] string? appId,
            [FromQuery] DateTimeOffset? fromDate,
            [FromQuery] DateTimeOffset? toDate,
            [FromQuery] string? status,
            [FromQuery] int? limit,
            [FromQuery] int? skip,
            IAuditLogService auditService,
            CancellationToken ct) =>
        {
            var query = new AuditLogQueryRequest
            {
                AppId = appId,
                FromDate = fromDate,
                ToDate = toDate,
                Status = status,
                Limit = limit ?? 50,
                Skip = skip ?? 0
            };

            var result = await auditService.QueryLogsAsync(query, ct);
            return Results.Ok(result);
        })
        .WithName("QueryAuditLogs")
        .WithSummary("Query persistent compliance audit logs from disk with date and app filtering");

        // Export compliance audit log as CSV
        group.MapGet("/audit/export", async (
            [FromQuery] string? appId,
            [FromQuery] DateTimeOffset? fromDate,
            [FromQuery] DateTimeOffset? toDate,
            [FromQuery] string? status,
            IAuditLogService auditService,
            CancellationToken ct) =>
        {
            var query = new AuditLogQueryRequest
            {
                AppId = appId,
                FromDate = fromDate,
                ToDate = toDate,
                Status = status
            };

            var csv = await auditService.ExportLogsCsvAsync(query, ct);
            var fileName = $"audit_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
        })
        .WithName("ExportAuditLogs")
        .WithSummary("Export persistent audit log trail as CSV file");

        // Organization Billing & Cost Governance Summary
        group.MapGet("/billing", async (IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var summary = await registry.GetBillingSummaryAsync(ct);
            return Results.Ok(summary);
        })
        .WithName("GetBillingSummary")
        .WithSummary("Get organization-wide financial summary and per-application token billing breakdown");

        // Export Billing & Cost Governance Report as CSV
        group.MapGet("/billing/export", async (IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var csv = await registry.ExportBillingCsvAsync(ct);
            var fileName = $"billing_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
        })
        .WithName("ExportBillingCsv")
        .WithSummary("Export application billing and token cost report as CSV spreadsheet");

        // List supported and available models across Bedrock and Local (Dynamic Live Sync with Fallback)
        group.MapGet("/models", async (
            ILocalModelService localService,
            ISTSService stsService,
            IOptions<GatewayOptions> gatewayOptions,
            CancellationToken ct) =>
        {
            var bedrockModels = new List<object>();
            var isLiveAws = false;

            try
            {
                var stsStatus = await stsService.GetStatusAsync(ct);
                if (stsStatus.IsInitialized)
                {
                    var credentials = await stsService.GetCredentialsAsync(ct);
                    var region = RegionEndpoint.GetBySystemName(gatewayOptions.Value.Aws.Region);
                    using var bedrockClient = new AmazonBedrockClient(credentials, region);

                    var response = await bedrockClient.ListFoundationModelsAsync(new ListFoundationModelsRequest
                    {
                        ByOutputModality = ModelModality.TEXT
                    }, ct);

                    if (response.ModelSummaries != null && response.ModelSummaries.Count > 0)
                    {
                        bedrockModels.AddRange(response.ModelSummaries
                            .Where(m => m.InferenceTypesSupported?.Contains(InferenceType.ON_DEMAND) == true || (m.InferenceTypesSupported?.Count ?? 0) == 0)
                            .OrderBy(m => m.ProviderName)
                            .ThenBy(m => m.ModelName)
                            .Select(m => {
                                var rate = ModelPricingCatalog.GetDefaultRate("bedrock", m.ModelId);
                                return new
                                {
                                    id = m.ModelId,
                                    name = $"{m.ProviderName} {m.ModelName} ({m.ModelId})",
                                    provider = "bedrock",
                                    contextWindow = 200000,
                                    isLive = true,
                                    defaultInputCost = rate.InputCostPerMillion,
                                    defaultOutputCost = rate.OutputCostPerMillion
                                };
                            }));
                        isLiveAws = true;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to comprehensive curated list if offline or unconfigured
            }

            if (bedrockModels.Count == 0)
            {
                var curated = new[]
                {
                    ("anthropic.claude-3-5-sonnet-20241022-v2:0", "Claude 3.5 Sonnet v2 (anthropic.claude-3-5-sonnet-20241022-v2:0)", 200000),
                    ("anthropic.claude-3-5-sonnet-20240620-v1:0", "Claude 3.5 Sonnet v1 (anthropic.claude-3-5-sonnet-20240620-v1:0)", 200000),
                    ("anthropic.claude-3-5-haiku-20241022-v1:0", "Claude 3.5 Haiku (anthropic.claude-3-5-haiku-20241022-v1:0)", 200000),
                    ("anthropic.claude-3-haiku-20240307-v1:0", "Claude 3 Haiku (anthropic.claude-3-haiku-20240307-v1:0)", 200000),
                    ("anthropic.claude-3-opus-20240229-v1:0", "Claude 3 Opus (anthropic.claude-3-opus-20240229-v1:0)", 200000),
                    ("amazon.nova-pro-v1:0", "Amazon Nova Pro (amazon.nova-pro-v1:0)", 300000),
                    ("amazon.nova-lite-v1:0", "Amazon Nova Lite (amazon.nova-lite-v1:0)", 300000),
                    ("amazon.nova-micro-v1:0", "Amazon Nova Micro (amazon.nova-micro-v1:0)", 128000),
                    ("meta.llama3-2-90b-instruct-v1:0", "Meta Llama 3.2 90B (meta.llama3-2-90b-instruct-v1:0)", 128000),
                    ("meta.llama3-2-11b-instruct-v1:0", "Meta Llama 3.2 11B (meta.llama3-2-11b-instruct-v1:0)", 128000),
                    ("meta.llama3-1-70b-instruct-v1:0", "Meta Llama 3.1 70B (meta.llama3-1-70b-instruct-v1:0)", 128000),
                    ("meta.llama3-1-8b-instruct-v1:0", "Meta Llama 3.1 8B (meta.llama3-1-8b-instruct-v1:0)", 128000),
                    ("meta.llama3-70b-instruct-v1:0", "Meta Llama 3 70B (meta.llama3-70b-instruct-v1:0)", 8192),
                    ("meta.llama3-8b-instruct-v1:0", "Meta Llama 3 8B (meta.llama3-8b-instruct-v1:0)", 8192),
                    ("mistral.mistral-large-2407-v1:0", "Mistral Large 2 (mistral.mistral-large-2407-v1:0)", 128000),
                    ("mistral.mistral-7b-instruct-v0:2", "Mistral 7B Instruct (mistral.mistral-7b-instruct-v0:2)", 32000),
                    ("mistral.mixtral-8x7b-instruct-v0:1", "Mixtral 8x7B (mistral.mixtral-8x7b-instruct-v0:1)", 32000),
                    ("cohere.command-r-plus-v1:0", "Cohere Command R+ (cohere.command-r-plus-v1:0)", 128000),
                    ("amazon.titan-text-express-v1", "Amazon Titan Text Express (amazon.titan-text-express-v1)", 8000)
                };

                bedrockModels.AddRange(curated.Select(c => {
                    var rate = ModelPricingCatalog.GetDefaultRate("bedrock", c.Item1);
                    return (object)new
                    {
                        id = c.Item1,
                        name = c.Item2,
                        provider = "bedrock",
                        contextWindow = c.Item3,
                        isLive = false,
                        defaultInputCost = rate.InputCostPerMillion,
                        defaultOutputCost = rate.OutputCostPerMillion
                    };
                }));
            }

            var localRaw = await localService.ListAvailableLocalModelsAsync(ct);
            var localModels = localRaw.Select(m => new
            {
                id = m,
                name = m,
                provider = "local",
                contextWindow = 8192,
                isLive = true,
                defaultInputCost = 0.00,
                defaultOutputCost = 0.00
            }).ToList();

            return Results.Ok(new
            {
                bedrock = bedrockModels,
                local = localModels,
                isLiveBedrockSynced = isLiveAws
            });
        })
        .WithName("GetModels");

        // Get STS Credential status
        group.MapGet("/credentials/status", async (ISTSService stsService, CancellationToken ct) =>
        {
            var status = await stsService.GetStatusAsync(ct);
            return Results.Ok(status);
        })
        .WithName("GetCredentialStatus");

        #endregion

        #region Guardrails Management Endpoints

        // Get Guardrail Configuration
        group.MapGet("/guardrails/config", (IGuardrailService guardrailService) =>
        {
            var config = guardrailService.GetCurrentOptions();
            return Results.Ok(config);
        })
        .WithName("GetGuardrailConfig")
        .WithSummary("Retrieve current enterprise safety guardrail rules and active mode");

        // Update Guardrail Configuration
        group.MapPut("/guardrails/config", ([FromBody] GuardrailOptions options, IGuardrailService guardrailService) =>
        {
            guardrailService.UpdateOptions(options);
            return Results.Ok(new { message = "Guardrail configuration updated successfully", config = options });
        })
        .WithName("UpdateGuardrailConfig")
        .WithSummary("Update enterprise guardrail rules, PCI/PII detectors, and enforcement mode (Block/Redact/Audit)");

        // Test text against Inbound Guardrail inspection sandbox
        group.MapPost("/guardrails/test", async (
            [FromBody] GuardrailTestRequest request,
            IGuardrailService guardrailService,
            CancellationToken ct) =>
        {
            var result = await guardrailService.EvaluateAsync(
                request.Input,
                modeOverride: request.Mode,
                cancellationToken: ct);

            return Results.Ok(result);
        })
        .WithName("TestGuardrails")
        .WithSummary("Interactive sandbox to test text against PCI, PII, Secrets, and Injection guardrails");

        // Test text against Outbound Model Response Guardrail inspection sandbox
        group.MapPost("/guardrails/test-output", async (
            [FromBody] GuardrailTestRequest request,
            IGuardrailService guardrailService,
            CancellationToken ct) =>
        {
            var result = await guardrailService.EvaluateOutputAsync(
                request.Input,
                modeOverride: request.Mode,
                cancellationToken: ct);

            return Results.Ok(result);
        })
        .WithName("TestOutputGuardrails")
        .WithSummary("Interactive sandbox to test simulated model response text for leaked credentials/PII");

        #endregion
    }
}
