using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class HostNetworkTrustTests
{
    [Theory]
    [InlineData("192.168.1.50", "192.168.1.0/24", true)]
    [InlineData("192.168.2.50", "192.168.1.0/24", false)]
    [InlineData("10.240.12.50", "10.240.12.50/32", true)]
    [InlineData("10.240.12.51", "10.240.12.50/32", false)]
    [InlineData("10.15.20.99", "10.0.0.0/8", true)]
    [InlineData("172.16.5.10", "172.16.0.0/12", true)]
    [InlineData("172.32.1.1", "172.16.0.0/12", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("::1", "::1", true)]
    public void IpNetworkHelper_MatchesCidr_CorrectlyEvaluates(string clientIpStr, string cidrRule, bool expected)
    {
        var clientIp = IPAddress.Parse(clientIpStr);
        var result = IpNetworkHelper.MatchesCidr(clientIp, cidrRule);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IpNetworkHelper_WhenAllowedCidrsEmpty_AllowsAnyIp()
    {
        var clientIp = IPAddress.Parse("203.0.113.195");
        var allowedCidrs = new List<string>();

        var result = IpNetworkHelper.IsIpInAllowedCidrs(clientIp, allowedCidrs);
        Assert.True(result);
    }

    [Fact]
    public void IpNetworkHelper_WhenIpMatchesAnyAllowedCidr_ReturnsTrue()
    {
        var clientIp = IPAddress.Parse("10.240.12.50");
        var allowedCidrs = new List<string> { "192.168.1.0/24", "10.240.12.0/24", "127.0.0.1/32" };

        var result = IpNetworkHelper.IsIpInAllowedCidrs(clientIp, allowedCidrs);
        Assert.True(result);
    }

    [Fact]
    public void IpNetworkHelper_WhenIpOutsideAllAllowedCidrs_ReturnsFalse()
    {
        var clientIp = IPAddress.Parse("203.0.113.88");
        var allowedCidrs = new List<string> { "192.168.1.0/24", "10.240.12.0/24" };

        var result = IpNetworkHelper.IsIpInAllowedCidrs(clientIp, allowedCidrs);
        Assert.False(result);
    }

    [Fact]
    public async Task ApplicationRegistry_AuthenticateApp_EnforcesHostIpWhitelist()
    {
        var mockSecurity = new Mock<ISecurityService>();
        mockSecurity.Setup(s => s.GenerateApiKey()).Returns(("ug_live_rawkey123", "hash123", "ug_live_rawk"));
        mockSecurity.Setup(s => s.VerifyKey("ug_live_rawkey123", "hash123")).Returns(true);
        mockSecurity.Setup(s => s.IssueAppStsToken(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .Returns(("ug_sts_dummy", DateTimeOffset.UtcNow.AddHours(1)));

        var options = Options.Create(new GatewayOptions
        {
            Storage = new StorageOptions { DataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            Security = new SecurityOptions { EnforceAppApiKey = true, AdminApiKey = "ug-admin-key" }
        });

        var service = new ApplicationRegistryService(mockSecurity.Object, options, NullLogger<ApplicationRegistryService>.Instance);

        var created = await service.CreateAppAsync(new CreateAppRequest
        {
            AppId = "secure-host-app",
            Name = "Secure Host App",
            AllowedCidrs = ["10.240.12.0/24", "127.0.0.1/32"]
        });

        // 1. Authenticate from authorized IP (10.240.12.50) -> Succeeded
        var (validFromAuthIp, _, failureReason1) = await service.AuthenticateAppAsync(
            "secure-host-app", 
            "ug_live_rawkey123", 
            IPAddress.Parse("10.240.12.50"));

        Assert.True(validFromAuthIp);
        Assert.Null(failureReason1);

        // 2. Authenticate from unauthorized IP (203.0.113.88) -> Rejected with HOST_IP_NOT_AUTHORIZED
        var (validFromRogueIp, _, failureReason2) = await service.AuthenticateAppAsync(
            "secure-host-app", 
            "ug_live_rawkey123", 
            IPAddress.Parse("203.0.113.88"));

        Assert.False(validFromRogueIp);
        Assert.Equal("HOST_IP_NOT_AUTHORIZED", failureReason2);

        // 3. Issue STS Token from rogue IP -> Rejected (returns null)
        var stsFromRogueIp = await service.IssueStsTokenForAppAsync(
            "secure-host-app", 
            "ug_live_rawkey123", 
            clientIp: IPAddress.Parse("203.0.113.88"));

        Assert.Null(stsFromRogueIp);

        // 4. Issue STS Token from authorized IP -> Succeeded
        var stsFromAuthIp = await service.IssueStsTokenForAppAsync(
            "secure-host-app", 
            "ug_live_rawkey123", 
            clientIp: IPAddress.Parse("10.240.12.50"));

        Assert.NotNull(stsFromAuthIp);
        Assert.Equal("secure-host-app", stsFromAuthIp.AppId);
    }
}
