using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class OktaSimulatorTests
{
    private readonly IOktaSimulatorService _oktaService;

    public OktaSimulatorTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            Security = new SecurityOptions
            {
                AdminApiKey = "test-secret-admin-key-2026"
            }
        });

        _oktaService = new OktaSimulatorService(options, NullLogger<OktaSimulatorService>.Instance);
    }

    [Fact]
    public async Task AuthenticateUserAsync_ForAdmin_ReturnsAdminAndDeveloperGroups()
    {
        var result = await _oktaService.AuthenticateUserAsync("wasim.khan@gmail.com");

        Assert.NotNull(result);
        Assert.NotEmpty(result.AccessToken);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal("wasim.khan@gmail.com", result.User.Email);
        Assert.Equal("Wasim Khan", result.User.FullName);
        Assert.True(result.User.IsAdmin);
        Assert.True(result.User.IsDeveloper);
        Assert.Contains(AdGroups.Admins, result.User.Groups);
        Assert.Contains(AdGroups.Developers, result.User.Groups);
    }

    [Fact]
    public async Task AuthenticateUserAsync_ForDeveloper_ReturnsOnlyDeveloperGroup()
    {
        var result = await _oktaService.AuthenticateUserAsync("dev-user@gmail.com");

        Assert.NotNull(result);
        Assert.NotEmpty(result.AccessToken);
        Assert.Equal("dev-user@gmail.com", result.User.Email);
        Assert.Equal("Developer User", result.User.FullName);
        Assert.False(result.User.IsAdmin);
        Assert.True(result.User.IsDeveloper);
        Assert.Contains(AdGroups.Developers, result.User.Groups);
        Assert.DoesNotContain(AdGroups.Admins, result.User.Groups);
    }

    [Fact]
    public async Task AuthenticateUserAsync_ForUnknownUser_ReturnsNull()
    {
        var result = await _oktaService.AuthenticateUserAsync("unknown.user@company.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateOktaJwt_WithValidAdminToken_SuccessfullyValidatesClaims()
    {
        var authResult = await _oktaService.AuthenticateUserAsync("wasim.khan@gmail.com");
        Assert.NotNull(authResult);

        var (isValid, principal, failureReason) = _oktaService.ValidateOktaJwt(authResult.AccessToken);

        Assert.True(isValid, failureReason);
        Assert.NotNull(principal);
        Assert.True(principal.IsInRole(AdGroups.Admins));
        Assert.True(principal.IsInRole(AdGroups.Developers));
        Assert.Equal("wasim.khan@gmail.com", principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
    }

    [Fact]
    public async Task ValidateOktaJwt_WithValidDeveloperToken_OnlyHasDeveloperRole()
    {
        var authResult = await _oktaService.AuthenticateUserAsync("dev-user@gmail.com");
        Assert.NotNull(authResult);

        var (isValid, principal, failureReason) = _oktaService.ValidateOktaJwt(authResult.AccessToken);

        Assert.True(isValid, failureReason);
        Assert.NotNull(principal);
        Assert.False(principal.IsInRole(AdGroups.Admins));
        Assert.True(principal.IsInRole(AdGroups.Developers));
        Assert.Equal("dev-user@gmail.com", principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
    }

    [Fact]
    public async Task ValidateOktaJwt_WithTamperedPayload_FailsSignatureVerification()
    {
        var authResult = await _oktaService.AuthenticateUserAsync("dev-user@gmail.com");
        Assert.NotNull(authResult);

        var parts = authResult.AccessToken.Split('.');
        Assert.Equal(3, parts.Length);

        // Tamper with payload
        var tamperedToken = $"{parts[0]}.eyJhZG1pbiI6dHJ1ZX0.{parts[2]}";
        var (isValid, principal, failureReason) = _oktaService.ValidateOktaJwt(tamperedToken);

        Assert.False(isValid);
        Assert.Null(principal);
        Assert.Contains("Signature verification failed", failureReason);
    }

    [Fact]
    public void ValidateOktaJwt_WithMalformedToken_FailsGracefully()
    {
        var (isValid1, _, reason1) = _oktaService.ValidateOktaJwt("");
        Assert.False(isValid1);
        Assert.Equal("Token is empty", reason1);

        var (isValid2, _, reason2) = _oktaService.ValidateOktaJwt("not-a-real-jwt");
        Assert.False(isValid2);
        Assert.Contains("Malformed JWT structure", reason2);
    }

    [Fact]
    public void GetSimulatedUsers_ReturnsBothAdminAndDevUsers()
    {
        var users = _oktaService.GetSimulatedUsers();

        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.Email == "wasim.khan@gmail.com" && u.Groups.Contains(AdGroups.Admins));
        Assert.Contains(users, u => u.Email == "dev-user@gmail.com" && !u.Groups.Contains(AdGroups.Admins));
    }
}
