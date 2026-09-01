using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class SecurityServiceTests
{
    private readonly ISecurityService _securityService;

    public SecurityServiceTests()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        _securityService = new SecurityService(dataProtectionProvider, NullLogger<SecurityService>.Instance);
    }

    [Fact]
    public void GenerateApiKey_ShouldReturnValidFormatAndMatchingHash()
    {
        var (rawKey, keyHash, keyPrefix) = _securityService.GenerateApiKey();

        Assert.StartsWith("ug_live_", rawKey);
        Assert.Equal(12, keyPrefix.Length);
        Assert.NotEmpty(keyHash);
        Assert.True(_securityService.VerifyKey(rawKey, keyHash));
    }

    [Fact]
    public void VerifyKey_WithInvalidKey_ShouldReturnFalse()
    {
        var (rawKey, keyHash, _) = _securityService.GenerateApiKey();
        var invalidKey = rawKey + "invalid";

        Assert.False(_securityService.VerifyKey(invalidKey, keyHash));
    }

    [Fact]
    public void EncryptAndDecrypt_ShouldPreserveOriginalText()
    {
        var secret = "arn:aws:iam::123456789012:role/BedrockExecutionRole";

        var encrypted = _securityService.Encrypt(secret);
        Assert.NotEqual(secret, encrypted);

        var decrypted = _securityService.Decrypt(encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void MaskSecret_ShouldMaskMiddleCharacters()
    {
        var secret = "arn:aws:iam::123456789012:role/BedrockExecutionRole";
        var masked = _securityService.MaskSecret(secret, 4);

        Assert.StartsWith("arn:", masked);
        Assert.EndsWith("Role", masked);
        Assert.Contains("******", masked);
    }
}
