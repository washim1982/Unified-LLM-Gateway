using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class AbuseControlsTests
{
    [Fact]
    public void GatewayOptions_DefaultAbuseClamps_AreConfigured()
    {
        var options = new GatewayOptions();

        Assert.Equal(50_000, options.Security.MaxInputCharacters);
        Assert.Equal(4_096, options.Security.MaxOutputTokensClamp);
        Assert.True(options.Security.EnableAuditLogPersistence);
    }

    [Fact]
    public async Task ModelRouter_WithOutputGuardrails_RedactsLeakedSecretsAndPii()
    {
        var mockBedrock = new Mock<IBedrockService>();
        var mockLocal = new Mock<ILocalModelService>();
        var mockRegistry = new Mock<IApplicationRegistryService>();
        var mockAuditLog = new Mock<IAuditLogService>();

        var guardrailOptions = Options.Create(new GatewayOptions
        {
            Guardrails = new GuardrailOptions
            {
                Enabled = true,
                Mode = GuardrailActionMode.Redact,
                ScanOutputs = true,
                OutputMode = GuardrailActionMode.Redact,
                Secrets = new SecretsGuardrailOptions { Enabled = true, MaskAwsKeys = true },
                Pci = new PciGuardrailOptions { Enabled = true, MaskCreditCards = true }
            }
        });

        var realGuardrailService = new GuardrailService(guardrailOptions, NullLogger<GuardrailService>.Instance);

        var app = new AppConfig
        {
            AppId = "secure-app",
            Name = "Secure App",
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20240620-v1:0",
            IsActive = true
        };

        mockRegistry.Setup(r => r.GetAppAsync("secure-app", It.IsAny<CancellationToken>()))
            .ReturnsAsync(app);

        // Bedrock response leaks AWS key and valid credit card
        mockBedrock.Setup(b => b.InvokeModelAsync(It.IsAny<UniversalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UniversalResponse
            {
                Output = "Here are the credentials: AKIAIOSFODNN7EXAMPLE and card 4111111111111111.",
                Model = app.Model,
                Provider = "bedrock",
                Tokens = new TokenUsage { Input = 20, Output = 40 }
            });

        var router = new ModelRouter(
            mockBedrock.Object,
            mockLocal.Object,
            mockRegistry.Object,
            realGuardrailService,
            mockAuditLog.Object,
            new PrometheusMetricsService(),
            guardrailOptions,
            NullLogger<ModelRouter>.Instance);

        var response = await router.RouteAppRequestAsync("secure-app", new InvokeAppRequest { Input = "Show credentials" });

        Assert.Null(response.Error);
        Assert.Contains("[REDACTED_AWS_KEY]", response.Output);
        Assert.Contains("[REDACTED_CREDIT_CARD]", response.Output);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", response.Output);
        Assert.DoesNotContain("4111111111111111", response.Output);

        mockAuditLog.Verify(a => a.LogRequest(It.Is<AuditLogRecord>(r =>
            r.OutputGuardrailAction == "Redacted" &&
            r.GuardrailViolations.Any(v => v.Contains("AWS_AccessKeyId"))
        )), Times.Once);
    }
}
