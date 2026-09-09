namespace UnifiedGateway.Services;

/// <summary>
/// Enterprise Hardware/Cloud KMS Cryptography and Secret Store Service.
/// Works with AWS KMS in TEST/PROD, and the Local AWS KMS Simulator (:5003) in local simulation.
/// </summary>
public interface IKmsCryptoService
{
    /// <summary>
    /// Encrypts plaintext using a KMS Customer Master Key (CMK) with AEAD authenticated encryption context.
    /// </summary>
    Task<string> EncryptAsync(string plaintext, string? keyId = null, Dictionary<string, string>? encryptionContext = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts a base64 ciphertext blob with AEAD encryption context integrity verification.
    /// </summary>
    Task<string> DecryptAsync(string ciphertextBlob, Dictionary<string, string>? encryptionContext = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves and decrypts a secret from the KMS Encrypted Secret Store.
    /// </summary>
    Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or updates an encrypted secret in the KMS Secret Store.
    /// </summary>
    Task<bool> StoreSecretAsync(string secretName, string secretValue, string? keyId = null, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Health probe for the KMS backend.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
