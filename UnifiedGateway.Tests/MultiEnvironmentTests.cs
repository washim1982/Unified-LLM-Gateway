using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class MultiEnvironmentTests
{
    [Fact]
    public void GatewayOptions_DefaultEnvironment_IsDevelopment()
    {
        var options = new GatewayOptions();

        Assert.Equal("Development", options.Environment);
        Assert.Equal("LocalDisk", options.Storage.StorageProvider);
        Assert.False(options.Security.EnableCloudKms);
        Assert.Equal(AwsAuthenticationType.Direct, options.Aws.ResolvedAuthType);
    }

    [Fact]
    public void GatewayOptions_ProfileAuthType_ResolvesToProfile()
    {
        var options = new GatewayOptions
        {
            Aws = new AwsOptions
            {
                AuthenticationType = "Profile",
                LocalProfileName = "dev-bedrock-profile"
            }
        };

        Assert.Equal(AwsAuthenticationType.Profile, options.Aws.ResolvedAuthType);
        Assert.Equal("dev-bedrock-profile", options.Aws.LocalProfileName);
    }

    [Fact]
    public void GatewayOptions_RolesAnywhereAuthType_ResolvesToRolesAnywhere()
    {
        var options = new GatewayOptions
        {
            Environment = "Production",
            Aws = new AwsOptions
            {
                AuthenticationType = "RolesAnywhere",
                RolesAnywhere = new RolesAnywhereOptions
                {
                    TrustAnchorArn = "arn:aws:rolesanywhere:us-east-1:123456789012:trust-anchor/prod-trust-anchor",
                    ProfileArn = "arn:aws:rolesanywhere:us-east-1:123456789012:profile/prod-profile",
                    RoleArn = "arn:aws:iam::123456789012:role/OnPremBedrockExecutionRole-Prod",
                    CertificatePath = "/etc/pki/gateway/client.crt",
                    PrivateKeyPath = "/etc/pki/gateway/client.key"
                }
            },
            Storage = new StorageOptions
            {
                StorageProvider = "HybridLokiS3",
                S3BucketName = "corp-unified-gateway-audit-prod"
            }
        };

        Assert.Equal(AwsAuthenticationType.RolesAnywhere, options.Aws.ResolvedAuthType);
        Assert.Equal("HybridLokiS3", options.Storage.StorageProvider);
        Assert.Equal("corp-unified-gateway-audit-prod", options.Storage.S3BucketName);
        Assert.Equal("arn:aws:rolesanywhere:us-east-1:123456789012:trust-anchor/prod-trust-anchor", options.Aws.RolesAnywhere.TrustAnchorArn);
    }

    [Fact]
    public async Task STSService_WithRolesAnywhereConfig_ReportsStatusCorrectly()
    {
        var options = Options.Create(new GatewayOptions
        {
            Environment = "Test",
            Aws = new AwsOptions
            {
                Region = "us-east-1",
                AuthenticationType = "RolesAnywhere",
                RolesAnywhere = new RolesAnywhereOptions
                {
                    TrustAnchorArn = "arn:aws:rolesanywhere:us-east-1:123456789012:trust-anchor/test-anchor",
                    ProfileArn = "arn:aws:rolesanywhere:us-east-1:123456789012:profile/test-profile",
                    RoleArn = "arn:aws:iam::123456789012:role/OnPremBedrockExecutionRole-Test"
                }
            }
        });

        var mockSecurityService = new Mock<ISecurityService>();
        mockSecurityService.Setup(s => s.MaskSecret(It.IsAny<string>(), It.IsAny<int>()))
            .Returns<string, int>((secret, visible) => $"{secret[..Math.Min(visible, secret.Length)]}***");

        var stsService = new STSService(options, mockSecurityService.Object, NullLogger<STSService>.Instance);

        var status = await stsService.GetStatusAsync();

        Assert.Equal("RolesAnywhere", status.AuthenticationType);
        Assert.Equal("us-east-1", status.Region);
        Assert.NotNull(status.RolesAnywhereProfileMasked);
        Assert.NotNull(status.TrustAnchorMasked);
    }
}
