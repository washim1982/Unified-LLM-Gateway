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
            .WithTags("Gateway Invocation & STS Tokens");

        #region Application STS Token Endpoints

        // Exchange long-term Application API Key or Master Admin Key for Short Temporary Secret (STS Token)
        group.MapPost("/sts/token", async (
            HttpContext httpContext,
            [FromHeader(Name = "X-API-Key")] string? xApiKey,
            [FromHeader(Name = "Authorization")] string? authHeader,
            IApplicationRegistryService registryService,
            CancellationToken ct) =>
        {
            AppStsTokenRequest? body = null;
            if (httpContext.Request.ContentLength > 0 || !string.IsNullOrEmpty(httpContext.Request.ContentType))
            {
                try
                {
                    body = await httpContext.Request.ReadFromJsonAsync<AppStsTokenRequest>(cancellationToken: ct);
                }
                catch
                {
                    // Gracefully handle if body or content-type is non-standard
                }
            }
            body ??= new AppStsTokenRequest();

            var apiKey = !string.IsNullOrWhiteSpace(body.ApiKey)
                ? body.ApiKey.Trim()
                : ExtractApiKey(xApiKey, authHeader);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.Json(new
                {
                    error = "UNAUTHORIZED",
                    message = "Missing API key. Provide long-term key in 'apiKey' body property, 'X-API-Key' header, or 'Authorization: Bearer <key>'."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var clientIp = ResolveClientIp(httpContext);

            var tokenResponse = await registryService.IssueStsTokenForAppAsync(
                body.AppId,
                apiKey,
                body.DurationSeconds,
                body.Scope,
                body.CallerId,
                clientIp,
                ct);

            if (tokenResponse == null)
            {
                if (!string.IsNullOrWhiteSpace(body.AppId))
                {
                    var targetApp = await registryService.GetAppAsync(body.AppId, ct);
                    if (targetApp != null && !registryService.ValidateAppHostIp(targetApp, clientIp))
                    {
                        return Results.Json(new
                        {
                            error = "FORBIDDEN",
                            code = "HOST_IP_NOT_AUTHORIZED",
                            message = $"Access Denied: Host IP '{clientIp}' is not in the authorized CIDR whitelist for application '{body.AppId}'."
                        }, statusCode: StatusCodes.Status403Forbidden);
                    }
                }

                return Results.Json(new
                {
                    error = "UNAUTHORIZED",
                    message = "Invalid Application API Key or Master Admin Key provided for STS token generation."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(tokenResponse);
        })
        .WithName("GenerateStsToken")
        .WithSummary("Exchange a long-term API key for a short temporary secret (STS token) with custom TTL")
        .Produces<AppStsTokenResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Inspect and decode an STS token claims & remaining TTL
        group.MapPost("/sts/inspect", async (
            HttpContext httpContext,
            [FromHeader(Name = "X-API-Key")] string? xApiKey,
            [FromHeader(Name = "Authorization")] string? authHeader,
            ISecurityService securityService,
            CancellationToken ct) =>
        {
            TokenInspectRequest? request = null;
            if (httpContext.Request.ContentLength > 0 || !string.IsNullOrEmpty(httpContext.Request.ContentType))
            {
                try
                {
                    request = await httpContext.Request.ReadFromJsonAsync<TokenInspectRequest>(cancellationToken: ct);
                }
                catch
                {
                }
            }

            var token = !string.IsNullOrWhiteSpace(request?.Token)
                ? request.Token.Trim()
                : ExtractApiKey(xApiKey, authHeader);

            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new { error = "Missing token to inspect." });
            }

            var inspection = securityService.InspectAppStsToken(token);
            return Results.Ok(inspection);
        })
        .WithName("InspectStsToken")
        .WithSummary("Inspect decoded claims, expiration, and validity of an Application STS token")
        .Produces<AppStsInspectResponse>(StatusCodes.Status200OK);

        #endregion

        #region Application Invocation Endpoints

        // Per-application generated endpoint (Accepts long-term API key OR short-term STS token)
        group.MapPost("/{appId}/invoke", async (
            string appId,
            [FromBody] InvokeAppRequest request,
            HttpContext httpContext,
            [FromHeader(Name = "X-API-Key")] string? xApiKey,
            [FromHeader(Name = "Authorization")] string? authHeader,
            IApplicationRegistryService registryService,
            IOptions<GatewayOptions> options,
            IModelRouter router,
            CancellationToken ct) =>
        {
            var apiKey = ExtractApiKey(xApiKey, authHeader);
            var clientIp = ResolveClientIp(httpContext);
            var (isValid, appConfig, failureReason) = await registryService.AuthenticateAppAsync(appId, apiKey, clientIp, ct);

            if (!isValid || appConfig == null)
            {
                if (failureReason == "HOST_IP_NOT_AUTHORIZED")
                {
                    return Results.Json(new UniversalResponse
                    {
                        Output = string.Empty,
                        AppId = appId,
                        Error = new GatewayError
                        {
                            Code = "HOST_IP_NOT_AUTHORIZED",
                            Message = $"Access Denied: Host IP '{clientIp}' is not in the authorized CIDR whitelist for application '{appId}'."
                        }
                    }, statusCode: StatusCodes.Status403Forbidden);
                }

                return Results.Json(new UniversalResponse
                {
                    Output = string.Empty,
                    AppId = appId,
                    Error = new GatewayError
                    {
                        Code = "UNAUTHORIZED",
                        Message = "Invalid, expired, or missing API key / STS token for this application. Pass via 'X-API-Key' or 'Authorization: Bearer <token>'."
                    }
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Abuse & Size Clamps
            var securityOpts = options.Value.Security;
            if (request.Input != null && request.Input.Length > securityOpts.MaxInputCharacters)
            {
                return Results.Json(new UniversalResponse
                {
                    Output = string.Empty,
                    AppId = appId,
                    Error = new GatewayError
                    {
                        Code = "PAYLOAD_TOO_LARGE",
                        Message = $"Input prompt character length ({request.Input.Length}) exceeds the configured maximum limit of {securityOpts.MaxInputCharacters} characters."
                    }
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var clampedRequest = request;
            if (request.MaxTokens.HasValue && request.MaxTokens.Value > securityOpts.MaxOutputTokensClamp)
            {
                clampedRequest = request with { MaxTokens = securityOpts.MaxOutputTokensClamp };
            }

            var response = await router.RouteAppRequestAsync(appId, clampedRequest, ct);

            if (response.Error != null && response.Error.Code == "GUARDRAIL_OUTPUT_BLOCKED")
            {
                return Results.Json(response, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Ok(response);
        })
        .WithName("InvokeApplication")
        .WithSummary("Invoke a registered AI application with auto-applied system prompt and model routing")
        .Produces<UniversalResponse>(StatusCodes.Status200OK)
        .Produces<UniversalResponse>(StatusCodes.Status401Unauthorized)
        .Produces<UniversalResponse>(StatusCodes.Status413PayloadTooLarge)
        .Produces<UniversalResponse>(StatusCodes.Status422UnprocessableEntity)
        .Produces<UniversalResponse>(StatusCodes.Status404NotFound);

        // Universal direct endpoint for admin/orchestrators (Accepts Admin Master API Key OR Admin STS Token)
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
                var isAuthorized = false;

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    if (apiKey.StartsWith("ug_sts_", StringComparison.OrdinalIgnoreCase))
                    {
                        var (isStsValid, payload, _) = securityService.ValidateAppStsToken(apiKey);
                        if (isStsValid && payload != null && payload.IsAdmin)
                        {
                            isAuthorized = true;
                        }
                    }
                    else
                    {
                        isAuthorized = securityService.VerifyKey(apiKey, securityService.HashKey(expectedKey));
                    }
                }

                if (!isAuthorized)
                {
                    return Results.Json(new UniversalResponse
                    {
                        Output = string.Empty,
                        Error = new GatewayError
                        {
                            Code = "ADMIN_UNAUTHORIZED",
                            Message = "Universal endpoint requires a valid Master Admin API Key or Admin STS Token."
                        }
                    }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            // Abuse & Size Clamps
            var securityOpts = options.Value.Security;
            if (request.Input != null && request.Input.Length > securityOpts.MaxInputCharacters)
            {
                return Results.Json(new UniversalResponse
                {
                    Output = string.Empty,
                    Error = new GatewayError
                    {
                        Code = "PAYLOAD_TOO_LARGE",
                        Message = $"Input prompt character length ({request.Input.Length}) exceeds the configured maximum limit of {securityOpts.MaxInputCharacters} characters."
                    }
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var clampedRequest = request;
            if (request.MaxTokens > securityOpts.MaxOutputTokensClamp)
            {
                clampedRequest = request with { MaxTokens = securityOpts.MaxOutputTokensClamp };
            }

            var response = await router.RouteAsync(clampedRequest, ct);

            if (response.Error != null && response.Error.Code == "GUARDRAIL_OUTPUT_BLOCKED")
            {
                return Results.Json(response, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Ok(response);
        })
        .WithName("InvokeUniversal")
        .WithSummary("Direct universal schema invocation with specified model and provider")
        .Produces<UniversalResponse>(StatusCodes.Status200OK)
        .Produces<UniversalResponse>(StatusCodes.Status401Unauthorized)
        .Produces<UniversalResponse>(StatusCodes.Status413PayloadTooLarge)
        .Produces<UniversalResponse>(StatusCodes.Status422UnprocessableEntity);

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

        #endregion
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

    private static System.Net.IPAddress? ResolveClientIp(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !string.IsNullOrWhiteSpace(forwardedFor))
        {
            var firstIp = forwardedFor.ToString().Split(',')[0].Trim();
            if (System.Net.IPAddress.TryParse(firstIp, out var parsedIp))
            {
                return parsedIp;
            }
        }

        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
        {
            if (System.Net.IPAddress.TryParse(realIp.ToString().Trim(), out var parsedRealIp))
            {
                return parsedRealIp;
            }
        }

        return context.Connection.RemoteIpAddress;
    }
}

public record TokenInspectRequest
{
    public string? Token { get; init; }
}
