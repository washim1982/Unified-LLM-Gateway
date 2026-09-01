namespace UnifiedGateway.Services;

public interface ISecurityService
{
    (string rawKey, string keyHash, string keyPrefix) GenerateApiKey();
    string HashKey(string apiKey);
    bool VerifyKey(string apiKey, string hash);
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    string MaskSecret(string? secret, int visibleChars = 4);
}
