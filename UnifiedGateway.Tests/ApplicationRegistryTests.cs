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

    public ApplicationRegistryTests()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        _securityService = new SecurityService(dataProtectionProvider, NullLogger<SecurityService>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), "ug-tests-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new GatewayOptions
        {
            Storage = new StorageOptions
            {
                DataDirectory = tempDir,
                RegistryFileName = "test_registry.json"
            }
        });

        _registryService = new ApplicationRegistryService(
            _securityService,
            options,
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

        var (isValidCorrect, app) = await _registryService.AuthenticateAppAsync("auth-test-app", created.ApiKey);
        Assert.True(isValidCorrect);
        Assert.NotNull(app);

        var (isValidWrong, _) = await _registryService.AuthenticateAppAsync("auth-test-app", "invalid-key");
        Assert.False(isValidWrong);
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
}
