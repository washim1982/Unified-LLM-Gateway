using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace UnifiedGateway.Services;

public class SecurityService : ISecurityService
{
    private readonly IDataProtector _protector;
    private readonly ILogger<SecurityService> _logger;

    public SecurityService(IDataProtectionProvider dataProtectionProvider, ILogger<SecurityService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector("UnifiedGateway.Security.SecretsProtector.v1");
        _logger = logger;
    }

    public (string rawKey, string keyHash, string keyPrefix) GenerateApiKey()
    {
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        var keySuffix = Convert.ToHexString(randomBytes).ToLowerInvariant();
        var rawKey = $"ug_live_{keySuffix}";
        var keyPrefix = rawKey[..12]; // e.g. "ug_live_a1b2"
        var keyHash = HashKey(rawKey);

        return (rawKey, keyHash, keyPrefix);
    }

    public string HashKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public bool VerifyKey(string apiKey, string hash)
    {
        var computed = HashKey(apiKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(hash)
        );
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            return _protector.Protect(plainText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt secret payload");
            throw;
        }
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt secret payload");
            throw;
        }
    }

    public string MaskSecret(string? secret, int visibleChars = 4)
    {
        if (string.IsNullOrEmpty(secret))
            return "******";

        if (secret.Length <= visibleChars)
            return "******";

        var prefix = secret[..visibleChars];
        return $"{prefix}******{secret[^visibleChars..]}";
    }
}
