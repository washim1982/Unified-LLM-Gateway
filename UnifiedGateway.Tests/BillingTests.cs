using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class BillingTests
{
    private readonly Mock<ISecurityService> _mockSecurity;
    private readonly IOptions<GatewayOptions> _options;

    public BillingTests()
    {
        _mockSecurity = new Mock<ISecurityService>();
        _mockSecurity.Setup(s => s.GenerateApiKey())
            .Returns(("raw_key_12345", "hash_12345", "ug_live_1234"));
        _mockSecurity.Setup(s => s.IssueAppStsToken(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns(("ug_sts_test_token", DateTimeOffset.UtcNow.AddHours(1)));

        var tempDir = Path.Combine(Path.GetTempPath(), "ugw_test_billing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        _options = Options.Create(new GatewayOptions
        {
            Storage = new StorageOptions
            {
                DataDirectory = tempDir,
                RegistryFileName = "test_apps.json"
            }
        });
    }

    [Fact]
    public void ModelPricingCatalog_ReturnsCorrectDefaults()
    {
        var sonnetRate = ModelPricingCatalog.GetDefaultRate("bedrock", "anthropic.claude-3-5-sonnet-20241022-v2:0");
        Assert.Equal(3.00, sonnetRate.InputCostPerMillion);
        Assert.Equal(15.00, sonnetRate.OutputCostPerMillion);

        var haikuRate = ModelPricingCatalog.GetDefaultRate("bedrock", "anthropic.claude-3-5-haiku-20241022-v1:0");
        Assert.Equal(0.80, haikuRate.InputCostPerMillion);
        Assert.Equal(4.00, haikuRate.OutputCostPerMillion);

        var novaRate = ModelPricingCatalog.GetDefaultRate("bedrock", "amazon.nova-pro-v1:0");
        Assert.Equal(0.80, novaRate.InputCostPerMillion);
        Assert.Equal(3.20, novaRate.OutputCostPerMillion);

        var localRate = ModelPricingCatalog.GetDefaultRate("local", "ollama/llama3");
        Assert.Equal(0.00, localRate.InputCostPerMillion);
        Assert.Equal(0.00, localRate.OutputCostPerMillion);
    }

    [Fact]
    public async Task CreateAppAsync_PersistsCustomTokenPricing()
    {
        var registry = new ApplicationRegistryService(_mockSecurity.Object, _options, NullLogger<ApplicationRegistryService>.Instance);

        var request = new CreateAppRequest
        {
            AppId = "custom-pricing-app",
            Name = "Custom Pricing App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20241022-v2:0",
            InputCostPerMillion = 2.50,
            OutputCostPerMillion = 12.00
        };

        var response = await registry.CreateAppAsync(request);
        Assert.NotNull(response.App);
        Assert.Equal(2.50, response.App.InputCostPerMillion);
        Assert.Equal(12.00, response.App.OutputCostPerMillion);

        var loaded = await registry.GetAppAsync("custom-pricing-app");
        Assert.NotNull(loaded);
        Assert.Equal(2.50, loaded.InputCostPerMillion);
        Assert.Equal(12.00, loaded.OutputCostPerMillion);
    }

    [Fact]
    public async Task GetBillingSummaryAsync_CalculatesAccurateCosts()
    {
        var registry = new ApplicationRegistryService(_mockSecurity.Object, _options, NullLogger<ApplicationRegistryService>.Instance);

        // Create app with $4.00/1M in and $10.00/1M out
        await registry.CreateAppAsync(new CreateAppRequest
        {
            AppId = "billing-calc-app",
            Name = "Billing Calculation App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20241022-v2:0",
            InputCostPerMillion = 4.00,
            OutputCostPerMillion = 10.00
        });

        // Record metrics: 500,000 in tokens, 200,000 out tokens
        // Expected in cost: (500,000 / 1,000,000) * 4.00 = $2.00
        // Expected out cost: (200,000 / 1,000,000) * 10.00 = $2.00
        // Expected total cost: $4.00
        await registry.RecordMetricAsync(new RequestLogEntry
        {
            AppId = "billing-calc-app",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20241022-v2:0",
            InputTokens = 500000,
            OutputTokens = 200000,
            Success = true,
            LatencyMs = 250
        });

        var summary = await registry.GetBillingSummaryAsync();
        Assert.NotNull(summary);

        var bill = summary.AppBills.FirstOrDefault(b => b.AppId == "billing-calc-app");
        Assert.NotNull(bill);
        Assert.Equal(500000, bill.InputTokens);
        Assert.Equal(200000, bill.OutputTokens);
        Assert.Equal(700000, bill.TotalTokens);
        Assert.Equal(2.0000m, bill.InputCostUsd);
        Assert.Equal(2.0000m, bill.OutputCostUsd);
        Assert.Equal(4.0000m, bill.TotalCostUsd);
        Assert.True(summary.TotalSpendUsd >= 4.0000m);
    }

    [Fact]
    public async Task UpdateAppAsync_UpdatesTokenPricing()
    {
        var registry = new ApplicationRegistryService(_mockSecurity.Object, _options, NullLogger<ApplicationRegistryService>.Instance);

        await registry.CreateAppAsync(new CreateAppRequest
        {
            AppId = "rate-update-app",
            Name = "Rate Update App",
            Provider = "bedrock",
            Model = "amazon.nova-pro-v1:0",
            InputCostPerMillion = 0.80,
            OutputCostPerMillion = 3.20
        });

        var updated = await registry.UpdateAppAsync("rate-update-app", new UpdateAppRequest
        {
            InputCostPerMillion = 0.50,
            OutputCostPerMillion = 2.00
        });

        Assert.NotNull(updated);
        Assert.Equal(0.50, updated.InputCostPerMillion);
        Assert.Equal(2.00, updated.OutputCostPerMillion);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public async Task ExportBillingCsvAsync_GeneratesValidCsvContent()
    {
        var registry = new ApplicationRegistryService(_mockSecurity.Object, _options, NullLogger<ApplicationRegistryService>.Instance);

        await registry.CreateAppAsync(new CreateAppRequest
        {
            AppId = "csv-test-app",
            Name = "CSV Test App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20241022-v2:0",
            InputCostPerMillion = 3.00,
            OutputCostPerMillion = 15.00
        });

        var csv = await registry.ExportBillingCsvAsync();
        Assert.NotNull(csv);
        Assert.Contains("App ID,Application Name,Provider,Model", csv);
        Assert.Contains("\"csv-test-app\"", csv);
        Assert.Contains("\"CSV Test App\"", csv);
        Assert.Contains("3.0000", csv);
        Assert.Contains("15.0000", csv);
    }
}
