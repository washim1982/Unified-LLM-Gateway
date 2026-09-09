using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class KmsCryptoService : IKmsCryptoService, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly ISTSService _stsService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<KmsCryptoService> _logger;
    private AmazonKeyManagementServiceClient? _kmsClient;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public KmsCryptoService(
        IOptions<GatewayOptions> options,
        ISTSService stsService,
        IHttpClientFactory httpClientFactory,
        ILogger<KmsCryptoService> logger)
    {
        _options = options.Value;
        _stsService = stsService;
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
    }

    private bool IsSimulatorMode => !string.IsNullOrWhiteSpace(_options.Aws.KmsEndpoint);

    private async Task<AmazonKeyManagementServiceClient> GetOrCreateKmsClientAsync(CancellationToken ct)
    {
        if (_kmsClient != null) return _kmsClient;

        await _lock.WaitAsync(ct);
        try
        {
            if (_kmsClient != null) return _kmsClient;

            var creds = await _stsService.GetCredentialsAsync(ct);
            var config = new AmazonKeyManagementServiceConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Aws.Region)
            };

            if (!string.IsNullOrWhiteSpace(_options.Aws.KmsEndpoint))
            {
                config.ServiceURL = _options.Aws.KmsEndpoint;
            }

            _kmsClient = new AmazonKeyManagementServiceClient(creds, config);
            return _kmsClient;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> EncryptAsync(string plaintext, string? keyId = null, Dictionary<string, string>? encryptionContext = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        var targetKey = keyId ?? _options.Security.KmsKeyId ?? "default-master-key";

        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.KmsEndpoint!.TrimEnd('/')}/encrypt";
                var payload = new
                {
                    KeyId = targetKey,
                    Plaintext = plaintext,
                    EncryptionContext = encryptionContext ?? new Dictionary<string, string>()
                };

                var res = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
                res.EnsureSuccessStatusCode();
                var result = await res.Content.ReadFromJsonAsync<KmsSimulatorEncryptResponse>(cancellationToken: cancellationToken);
                return result?.CiphertextBlob ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encrypt payload using KMS Simulator (:5003)");
                throw;
            }
        }

        // Standard AWS Cloud KMS in TEST / PROD
        try
        {
            var client = await GetOrCreateKmsClientAsync(cancellationToken);
            var req = new EncryptRequest
            {
                KeyId = targetKey,
                Plaintext = new MemoryStream(Encoding.UTF8.GetBytes(plaintext)),
                EncryptionContext = encryptionContext ?? new Dictionary<string, string>()
            };

            var resp = await client.EncryptAsync(req, cancellationToken);
            return Convert.ToBase64String(resp.CiphertextBlob.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt payload using AWS Cloud KMS");
            throw;
        }
    }

    public async Task<string> DecryptAsync(string ciphertextBlob, Dictionary<string, string>? encryptionContext = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ciphertextBlob)) return string.Empty;

        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.KmsEndpoint!.TrimEnd('/')}/decrypt";
                var payload = new
                {
                    CiphertextBlob = ciphertextBlob,
                    EncryptionContext = encryptionContext ?? new Dictionary<string, string>()
                };

                var res = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
                res.EnsureSuccessStatusCode();
                var result = await res.Content.ReadFromJsonAsync<KmsSimulatorDecryptResponse>(cancellationToken: cancellationToken);
                return result?.Plaintext ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt payload using KMS Simulator (:5003)");
                throw;
            }
        }

        // Standard AWS Cloud KMS in TEST / PROD
        try
        {
            var client = await GetOrCreateKmsClientAsync(cancellationToken);
            var req = new DecryptRequest
            {
                CiphertextBlob = new MemoryStream(Convert.FromBase64String(ciphertextBlob)),
                EncryptionContext = encryptionContext ?? new Dictionary<string, string>()
            };

            var resp = await client.DecryptAsync(req, cancellationToken);
            return Encoding.UTF8.GetString(resp.Plaintext.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt payload using AWS Cloud KMS");
            throw;
        }
    }

    public async Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName)) return null;

        if (IsSimulatorMode)
        {
            try
            {
                var cleanName = secretName.TrimStart('/');
                var url = $"{_options.Aws.KmsEndpoint!.TrimEnd('/')}/secrets/{cleanName}";
                var res = await _httpClient.GetAsync(url, cancellationToken);
                if (!res.IsSuccessStatusCode) return null;

                var result = await res.Content.ReadFromJsonAsync<KmsSimulatorSecretResponse>(cancellationToken: cancellationToken);
                return result?.SecretValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve secret '{SecretName}' from KMS Simulator", secretName);
                return null;
            }
        }

        // In AWS Cloud, secrets can be fetched from AWS Secrets Manager / KMS
        return null;
    }

    public async Task<bool> StoreSecretAsync(string secretName, string secretValue, string? keyId = null, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName) || string.IsNullOrEmpty(secretValue)) return false;

        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.KmsEndpoint!.TrimEnd('/')}/secrets";
                var payload = new
                {
                    SecretName = secretName.StartsWith("/") ? secretName : $"/{secretName}",
                    SecretValue = secretValue,
                    KeyId = keyId ?? _options.Security.KmsKeyId ?? "default-master-key",
                    Description = description ?? "Managed by Unified LLM Gateway"
                };

                var res = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store secret '{SecretName}' in KMS Simulator", secretName);
                return false;
            }
        }

        return true;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.KmsEndpoint!.TrimEnd('/')}/health";
                var res = await _httpClient.GetAsync(url, cancellationToken);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _kmsClient?.Dispose();
        _lock.Dispose();
    }

    private class KmsSimulatorEncryptResponse
    {
        [JsonPropertyName("CiphertextBlob")]
        public string CiphertextBlob { get; set; } = string.Empty;

        [JsonPropertyName("KeyId")]
        public string KeyId { get; set; } = string.Empty;
    }

    private class KmsSimulatorDecryptResponse
    {
        [JsonPropertyName("Plaintext")]
        public string Plaintext { get; set; } = string.Empty;

        [JsonPropertyName("KeyId")]
        public string KeyId { get; set; } = string.Empty;
    }

    private class KmsSimulatorSecretResponse
    {
        [JsonPropertyName("SecretName")]
        public string SecretName { get; set; } = string.Empty;

        [JsonPropertyName("SecretValue")]
        public string SecretValue { get; set; } = string.Empty;
    }
}
