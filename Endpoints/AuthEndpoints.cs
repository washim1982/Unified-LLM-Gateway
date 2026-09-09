using Microsoft.AspNetCore.Mvc;
using UnifiedGateway.Models;
using UnifiedGateway.Services;

namespace UnifiedGateway.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Okta Authentication Simulation");

        // Login / Issue Okta JWT for simulated users
        group.MapPost("/login", async (
            [FromBody] LoginRequest request,
            IOktaSimulatorService oktaService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.BadRequest(new { error = "Email address is required." });
            }

            var tokenResponse = await oktaService.AuthenticateUserAsync(request.Email, request.Password, ct);
            if (tokenResponse == null)
            {
                return Results.Json(new
                {
                    error = "INVALID_CREDENTIALS",
                    message = $"Simulated user '{request.Email}' not found. Available users: 'wasim.khan@gmail.com' (Admin) or 'dev-user@gmail.com' (Developer)."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(tokenResponse);
        })
        .WithName("OktaLogin")
        .WithSummary("Log in as a simulated Okta user and receive an RFC-7519 compliant fake Okta JWT")
        .Produces<OktaTokenResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // List available simulated Okta users
        group.MapGet("/users", (IOktaSimulatorService oktaService) =>
        {
            var users = oktaService.GetSimulatedUsers();
            return Results.Ok(users);
        })
        .WithName("ListSimulatedUsers")
        .WithSummary("List hard-coded simulated Okta identities with their Active Directory group memberships")
        .Produces<List<SimulatedUser>>(StatusCodes.Status200OK);

        // Inspect currently active session from Bearer token
        group.MapGet("/me", (
            HttpContext httpContext,
            IOktaSimulatorService oktaService) =>
        {
            var authHeader = httpContext.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "UNAUTHORIZED", message = "Missing or invalid 'Authorization: Bearer <okta_jwt>' header." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var token = authHeader[7..].Trim();
            var (isValid, principal, failureReason) = oktaService.ValidateOktaJwt(token);

            if (!isValid || principal == null)
            {
                return Results.Json(new { error = "INVALID_TOKEN", message = failureReason }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;
            var sub = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            var name = principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? string.Empty;
            var groups = principal.FindAll("groups").Select(c => c.Value).ToList();

            var profile = new OktaUserProfile
            {
                UserId = sub,
                Email = email,
                FullName = name,
                Groups = groups
            };

            return Results.Ok(profile);
        })
        .WithName("GetCurrentOktaUser")
        .WithSummary("Retrieve user details and AD groups from the active Okta Bearer token")
        .Produces<OktaUserProfile>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Standard OIDC OpenID Configuration Discovery Simulation
        app.MapGet("/.well-known/openid-configuration", (IOktaSimulatorService oktaService) =>
        {
            return Results.Ok(new
            {
                issuer = oktaService.Issuer,
                authorization_endpoint = $"{oktaService.Issuer}/v1/authorize",
                token_endpoint = "/api/auth/login",
                userinfo_endpoint = "/api/auth/me",
                jwks_uri = $"{oktaService.Issuer}/v1/keys",
                response_types_supported = new[] { "code", "token", "id_token" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "HS256" },
                scopes_supported = new[] { "openid", "profile", "email", "groups" },
                claims_supported = new[] { "sub", "email", "name", "preferred_username", "groups", "iss", "aud" }
            });
        })
        .WithTags("Okta Authentication Simulation")
        .ExcludeFromDescription();
    }
}
