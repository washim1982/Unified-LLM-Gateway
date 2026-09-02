using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class ApplicationRegistryTests
{
    private readonly ApplicationRegistryService _registryService;
    private readonly ISecurityService _securityService;
    private readonly GatewayOptions _options;

    public ApplicationRegistryTests()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        _securityService = new SecurityService(dataProtectionProvider, NullLogger<SecurityService>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), "ug-tests-" + Guid.NewGuid().ToString("N"));
        _options = new GatewayOptions
        {
            Security = new SecurityOptions
            {
                AdminApiKey = "ug-test-admin-secret-key",
                EnforceAppApiKey = true
            },
            Storage = new StorageOptions
            {
                DataDirectory = tempDir,
                RegistryFileName = "test_registry.json"
            }
        };

        _registryService = new ApplicationRegistryService(
            _securityService,
            Options.Create(_options),
            NullLogger<ApplicationRegistryService>.Instance);
    }

    [Fact]
    public async Task CreateApp_GeneratesKeyAndPersistsApp()
    {
        var createReq = new CreateAppRequest
        {
            AppId = "unit-test-app",
            Name = "Unit Test App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20240620-v1:0",
            SystemPrompt = "Test prompt",
            Temperature = 0.4,
            MaxTokens = 1000
        };

        var response = await _registryService.CreateAppAsync(createReq);

        Assert.NotNull(response);
        Assert.Equal("unit-test-app", response.App.AppId);
        Assert.StartsWith("ug_live_", response.ApiKey);
        Assert.Equal("/gateway/unit-test-app/invoke", response.EndpointUrl);
        Assert.StartsWith("ug_sts_", response.StsToken);
        Assert.True(response.StsExpiresAt > DateTimeOffset.UtcNow);

        var retrieved = await _registryService.GetAppAsync("unit-test-app");
        Assert.NotNull(retrieved);
        Assert.Equal("Unit Test App", retrieved.Name);
    }

    [Fact]
    public async Task AuthenticateApp_ValidatesCorrectKey()
    {
        var createReq = new CreateAppRequest
        {
            AppId = "auth-test-app",
            Name = "Auth Test App",
            Provider = "local",
            Model = "ollama/llama3"
        };

        var created = await _registryService.CreateAppAsync(createReq);

        var (isValidCorrect, app, _) = await _registryService.AuthenticateAppAsync("auth-test-app", created.ApiKey);
        Assert.True(isValidCorrect);
        Assert.NotNull(app);

        var (isValidWrong, _, _) = await _registryService.AuthenticateAppAsync("auth-test-app", "invalid-key");
        Assert.False(isValidWrong);
    }

    [Fact]
    public async Task AuthenticateApp_WithAppStsToken_Succeeds()
    {
        var createReq = new CreateAppRequest
        {
            AppId = "sts-auth-app",
            Name = "STS Auth App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20240620-v1:0"
        };

        var created = await _registryService.CreateAppAsync(createReq);

        // Authenticate using the generated initial STS token
        var (isValidSts, app, _) = await _registryService.AuthenticateAppAsync("sts-auth-app", created.StsToken);
        Assert.True(isValidSts);
        Assert.NotNull(app);
        Assert.Equal("sts-auth-app", app.AppId);
    }

    [Fact]
    public async Task AuthenticateApp_WithWrongAppStsToken_RejectsCrossAppUsage()
    {
        var app1 = await _registryService.CreateAppAsync(new CreateAppRequest { AppId = "app-one", Name = "App One" });
        var app2 = await _registryService.CreateAppAsync(new CreateAppRequest { AppId = "app-two", Name = "App Two" });

        // Attempt to invoke app-two using app-one's STS token
        var (isValid, _, _) = await _registryService.AuthenticateAppAsync("app-two", app1.StsToken);
        Assert.False(isValid);
    }

    [Fact]
    public async Task AuthenticateApp_WithAdminStsToken_AuthenticatesAnyApp()
    {
        await _registryService.CreateAppAsync(new CreateAppRequest { AppId = "target-app", Name = "Target App" });

        var (adminToken, _) = _securityService.IssueAppStsToken("*", TimeSpan.FromMinutes(30), "invoke", isAdmin: true);

        var (isValid, app, _) = await _registryService.AuthenticateAppAsync("target-app", adminToken);
        Assert.True(isValid);
        Assert.NotNull(app);
    }

    [Fact]
    public async Task IssueStsTokenForAppAsync_WithValidApiKey_ReturnsValidStsToken()
    {
        var created = await _registryService.CreateAppAsync(new CreateAppRequest
        {
            AppId = "exchange-app",
            Name = "Exchange Test App"
        });

        var tokenResp = await _registryService.IssueStsTokenForAppAsync(
            appId: "exchange-app",
            apiKey: created.ApiKey,
            durationSeconds: 1800,
            scope: "invoke",
            callerId: "service-worker-42");

        Assert.NotNull(tokenResp);
        Assert.StartsWith("ug_sts_", tokenResp.Token);
        Assert.Equal("exchange-app", tokenResp.AppId);
        Assert.Equal(1800, tokenResp.DurationSeconds);
        Assert.False(tokenResp.IsAdmin);

        // Verify the newly exchanged STS token works for authentication
        var (isValid, _, _) = await _registryService.AuthenticateAppAsync("exchange-app", tokenResp.Token);
        Assert.True(isValid);
    }

    [Fact]
    public async Task IssueStsTokenForAppAsync_WithAdminKey_ReturnsAdminStsToken()
    {
        var tokenResp = await _registryService.IssueStsTokenForAppAsync(
            appId: "*",
            apiKey: "ug-test-admin-secret-key",
            durationSeconds: 3600);

        Assert.NotNull(tokenResp);
        Assert.StartsWith("ug_sts_", tokenResp.Token);
        Assert.True(tokenResp.IsAdmin);
        Assert.Equal("*", tokenResp.AppId);
    }

    [Fact]
    public async Task IssueStsTokenForAppAsync_WithInvalidKey_ReturnsNull()
    {
        var tokenResp = await _registryService.IssueStsTokenForAppAsync(
            appId: "any-app",
            apiKey: "wrong-secret-key",
            durationSeconds: 3600);

        Assert.Null(tokenResp);
    }

    [Fact]
    public async Task UpdateApp_IncrementsVersionAndAppendsHistory()
    {
        var createReq = new CreateAppRequest
        {
            AppId = "version-test-app",
            Name = "Version Test App",
            SystemPrompt = "Version 1 prompt"
        };

        var created = await _registryService.CreateAppAsync(createReq);
        Assert.Equal(1, created.App.Version);

        var updated = await _registryService.UpdateAppAsync("version-test-app", new UpdateAppRequest
        {
            SystemPrompt = "Version 2 prompt with refinements"
        });

        Assert.NotNull(updated);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Version 2 prompt with refinements", updated.SystemPrompt);
        Assert.Single(updated.VersionHistory);
        Assert.Equal("Version 1 prompt", updated.VersionHistory[0].SystemPrompt);
    }

    [Fact]
    public async Task RotateAppApiKeyAsync_MaintainsDualKeyGracePeriodAndRevocation()
    {
        // 1. Create App with initial primary key
        var createReq = new CreateAppRequest
        {
            AppId = "rotation-test-app",
            Name = "Rotation App",
            SystemPrompt = "Prompt"
        };
        var created = await _registryService.CreateAppAsync(createReq);
        var oldKey = created.ApiKey;

        // Verify old key works initially
        var (isOldValidBefore, _, _) = await _registryService.AuthenticateAppAsync("rotation-test-app", oldKey);
        Assert.True(isOldValidBefore);

        // 2. Rotate Key with 7-day grace period
        var rotateResp = await _registryService.RotateAppApiKeyAsync("rotation-test-app", gracePeriodDays: 7);
        Assert.NotNull(rotateResp);
        var newKey = rotateResp.NewApiKey;
        Assert.NotEqual(oldKey, newKey);
        Assert.NotNull(rotateResp.SecondaryKeyExpiresAt);
        Assert.True(rotateResp.SecondaryKeyExpiresAt > DateTimeOffset.UtcNow);

        // 3. Verify BOTH new primary key AND old secondary key work during grace period
        var (isNewValid, _, _) = await _registryService.AuthenticateAppAsync("rotation-test-app", newKey);
        var (isOldValidDuringGrace, _, _) = await _registryService.AuthenticateAppAsync("rotation-test-app", oldKey);
        Assert.True(isNewValid);
        Assert.True(isOldValidDuringGrace);

        // 4. Verify emergency revocation of secondary key
        var revokeResp = await _registryService.RevokeSecondaryApiKeyAsync("rotation-test-app");
        Assert.NotNull(revokeResp);

        var (isOldValidAfterRevoke, _, _) = await _registryService.AuthenticateAppAsync("rotation-test-app", oldKey);
        var (isNewStillValid, _, _) = await _registryService.AuthenticateAppAsync("rotation-test-app", newKey);
        Assert.False(isOldValidAfterRevoke);
        Assert.True(isNewStillValid);
    }
}
