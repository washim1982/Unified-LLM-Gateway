using System.Security.Claims;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IOktaSimulatorService
{
    string Issuer { get; }
    string Audience { get; }

    Task<OktaTokenResponse?> AuthenticateUserAsync(string email, string? password = null, CancellationToken cancellationToken = default);
    (bool isValid, ClaimsPrincipal? principal, string? failureReason) ValidateOktaJwt(string token);
    IReadOnlyList<SimulatedUser> GetSimulatedUsers();
    SimulatedUser? GetUserByEmail(string email);
}
