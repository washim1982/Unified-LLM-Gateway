using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;
using UnifiedGateway.Services;

namespace UnifiedGateway.Endpoints;

public static class GatewayEndpoints
{
    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gateway")
            .WithTags("Gateway Invocation");

        // Per-application generated endpoint
        group.MapPost("/{appId}/invoke", async (
            string appId,
            [FromBody] InvokeAppRequest request,
            [FromHeader(Name = "X-API-Key")] string? xApiKey,
            [FromHeader(Name = "Authorization")] string? authHeader,
            IApplicationRegistryService registryService,
            IModelRouter router,
            CancellationToken ct) =>
        {
            var apiKey = ExtractApiKey(xApiKey, authHeader);
            var (isValid, appConfig) = await registryService.AuthenticateAppAsync(appId, apiKey, ct);

            if (!isValid || appConfig == null)
            {
                return Results.Json(new UniversalResponse
                {
                    Output = string.Empty,
                    AppId = appId,
                    Error = new GatewayError
                    {
                        Code = "UNAUTHORIZED",
                        Message = "Invalid or missing API key for this application. Pass via 'X-API-Key' or 'Authorization: Bearer <key>'."
                    }
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var response = await router.RouteAppRequestAsync(appId, request, ct);
            return Results.Ok(response);
        })
        .WithName("InvokeApplication")
        .WithSummary("Invoke a registered AI application with auto-applied system prompt and model routing")
        .Produces<UniversalResponse>(StatusCodes.Status200OK)
        .Produces<UniversalResponse>(StatusCodes.Status401Unauthorized)
        .Produces<UniversalResponse>(StatusCodes.Status404NotFound);

        // Universal direct endpoint for admin/orchestrators
        group.MapPost("/universal/invoke", async (
            [FromBody] UniversalRequest request,
            [FromHeader(Name = "X-API-Key")] string? xApiKey,
            [FromHeader(Name = "Authorization")] string? authHeader,
            IOptions<GatewayOptions> options,
            ISecurityService securityService,
            IModelRouter router,
            CancellationToken ct) =>
        {
            var apiKey = ExtractApiKey(xApiKey, authHeader);
            var expectedKey = options.Value.Security.AdminApiKey;

            if (options.Value.Security.EnforceAppApiKey)
            {
                if (string.IsNullOrWhiteSpace(apiKey) || !securityService.VerifyKey(apiKey, securityService.HashKey(expectedKey)))
                {
                    return Results.Json(new UniversalResponse
                    {
                        Output = string.Empty,
                        Error = new GatewayError
                        {
                            Code = "ADMIN_UNAUTHORIZED",
                            Message = "Universal endpoint requires a valid Master Admin API Key."
                        }
                    }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            var response = await router.RouteAsync(request, ct);
            return Results.Ok(response);
        })
        .WithName("InvokeUniversal")
        .WithSummary("Direct universal schema invocation with specified model and provider")
        .Produces<UniversalResponse>(StatusCodes.Status200OK)
        .Produces<UniversalResponse>(StatusCodes.Status401Unauthorized);

        // Gateway health & backend status check
        group.MapGet("/health", async (
            ISTSService stsService,
            ILocalModelService localModelService,
            CancellationToken ct) =>
        {
            var awsStatus = await stsService.GetStatusAsync(ct);
            var localStatus = await localModelService.ProbeStatusAsync(ct);

            var isHealthy = awsStatus.IsInitialized || localStatus.Values.Any(v => v);

            return Results.Ok(new
            {
                status = isHealthy ? "Healthy" : "Degraded",
                timestamp = DateTimeOffset.UtcNow,
                aws = awsStatus,
                localBackends = localStatus
            });
        })
        .WithName("GatewayHealth")
        .WithSummary("Probe STS credentials and local backend health");
    }

    private static string ExtractApiKey(string? xApiKey, string? authHeader)
    {
        if (!string.IsNullOrWhiteSpace(xApiKey))
            return xApiKey.Trim();

        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return authHeader[7..].Trim();
            return authHeader.Trim();
        }

        return string.Empty;
    }
}
