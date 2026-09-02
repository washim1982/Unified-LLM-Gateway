using Microsoft.AspNetCore.Mvc;
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

        // Real-time metrics and analytics summary
        group.MapGet("/metrics", async (IApplicationRegistryService registry, CancellationToken ct) =>
        {
            var summary = await registry.GetMetricsSummaryAsync(ct);
            return Results.Ok(summary);
        })
        .WithName("GetMetrics");

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

        // List supported and available models across Bedrock and Local
        group.MapGet("/models", async (ILocalModelService localService, CancellationToken ct) =>
        {
            var bedrockModels = new[]
            {
                new { id = "anthropic.claude-3-5-sonnet-20240620-v1:0", name = "Claude 3.5 Sonnet", provider = "bedrock", contextWindow = 200000 },
                new { id = "anthropic.claude-3-haiku-20240307-v1:0", name = "Claude 3 Haiku", provider = "bedrock", contextWindow = 200000 },
                new { id = "anthropic.claude-3-opus-20240229-v1:0", name = "Claude 3 Opus", provider = "bedrock", contextWindow = 200000 },
                new { id = "meta.llama3-70b-instruct-v1:0", name = "Meta Llama 3 70B", provider = "bedrock", contextWindow = 8192 },
                new { id = "meta.llama3-8b-instruct-v1:0", name = "Meta Llama 3 8B", provider = "bedrock", contextWindow = 8192 },
                new { id = "mistral.mistral-7b-instruct-v0:2", name = "Mistral 7B Instruct", provider = "bedrock", contextWindow = 32000 },
                new { id = "mistral.mixtral-8x7b-instruct-v0:1", name = "Mixtral 8x7B", provider = "bedrock", contextWindow = 32000 },
                new { id = "amazon.titan-text-express-v1", name = "Amazon Titan Text Express", provider = "bedrock", contextWindow = 8000 }
            };

            var localModels = await localService.ListAvailableLocalModelsAsync(ct);

            return Results.Ok(new
            {
                bedrock = bedrockModels,
                local = localModels
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
