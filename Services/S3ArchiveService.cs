using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class S3ArchiveService : IS3ArchiveService, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly ISTSService _stsService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<S3ArchiveService> _logger;
    private AmazonS3Client? _s3Client;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _verifiedBuckets = new(StringComparer.OrdinalIgnoreCase);

    public S3ArchiveService(
        IOptions<GatewayOptions> options,
        ISTSService stsService,
        IHttpClientFactory httpClientFactory,
        ILogger<S3ArchiveService> logger)
    {
        _options = options.Value;
        _stsService = stsService;
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
    }

    private bool IsSimulatorMode => !string.IsNullOrWhiteSpace(_options.Aws.S3Endpoint);

    private async Task<AmazonS3Client> GetOrCreateS3ClientAsync(CancellationToken ct)
    {
        if (_s3Client != null) return _s3Client;

        await _lock.WaitAsync(ct);
        try
        {
            if (_s3Client != null) return _s3Client;

            var creds = await _stsService.GetCredentialsAsync(ct);
            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Aws.Region),
                ForcePathStyle = true
            };

            if (!string.IsNullOrWhiteSpace(_options.Aws.S3Endpoint))
            {
                config.ServiceURL = _options.Aws.S3Endpoint;
            }

            _s3Client = new AmazonS3Client(creds, config);
            return _s3Client;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EnsureBucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || _verifiedBuckets.Contains(bucketName)) return;

        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.S3Endpoint!.TrimEnd('/')}/buckets/{bucketName}";
                var res = await _httpClient.PostAsync(url, null, cancellationToken);
                // 201 Created or 409 Conflict (already exists) are both considered valid
                if (res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    _verifiedBuckets.Add(bucketName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ensure bucket '{Bucket}' on S3 Simulator (:5002)", bucketName);
            }
            return;
        }

        // Standard AWS Cloud S3 in TEST / PROD
        try
        {
            var client = await GetOrCreateS3ClientAsync(cancellationToken);
            try
            {
                await client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = bucketName,
                    UseClientRegion = true
                }, cancellationToken);
                _verifiedBuckets.Add(bucketName);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
            {
                _verifiedBuckets.Add(bucketName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed ensuring bucket '{Bucket}' on AWS Cloud S3", bucketName);
        }
    }

    public async Task<bool> UploadAuditBatchAsync(string bucketName, string key, byte[] content, string contentType = "application/json", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(key) || content == null) return false;

        await EnsureBucketExistsAsync(bucketName, cancellationToken);

        if (IsSimulatorMode)
        {
            try
            {
                var cleanKey = key.TrimStart('/');
                var url = $"{_options.Aws.S3Endpoint!.TrimEnd('/')}/buckets/{bucketName}/objects/{cleanKey}";
                using var byteContent = new ByteArrayContent(content);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                var res = await _httpClient.PutAsync(url, byteContent, cancellationToken);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed uploading audit object to S3 Simulator (:5002) - {Bucket}/{Key}", bucketName, key);
                return false;
            }
        }

        // Standard AWS Cloud S3 in TEST / PROD
        try
        {
            var client = await GetOrCreateS3ClientAsync(cancellationToken);
            using var stream = new MemoryStream(content);
            var req = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = false
            };

            var resp = await client.PutObjectAsync(req, cancellationToken);
            return resp.HttpStatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed uploading audit object to AWS S3 - {Bucket}/{Key}", bucketName, key);
            return false;
        }
    }

    public async Task<string?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(key)) return null;

        if (IsSimulatorMode)
        {
            try
            {
                var cleanKey = key.TrimStart('/');
                var url = $"{_options.Aws.S3Endpoint!.TrimEnd('/')}/buckets/{bucketName}/objects/{cleanKey}";
                var res = await _httpClient.GetAsync(url, cancellationToken);
                if (!res.IsSuccessStatusCode) return null;

                return await res.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed downloading object from S3 Simulator (:5002) - {Bucket}/{Key}", bucketName, key);
                return null;
            }
        }

        // Standard AWS Cloud S3 in TEST / PROD
        try
        {
            var client = await GetOrCreateS3ClientAsync(cancellationToken);
            var resp = await client.GetObjectAsync(bucketName, key, cancellationToken);
            using var reader = new StreamReader(resp.ResponseStream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed downloading object from AWS S3 - {Bucket}/{Key}", bucketName, key);
            return null;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsSimulatorMode)
        {
            try
            {
                var url = $"{_options.Aws.S3Endpoint!.TrimEnd('/')}/health";
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
        _s3Client?.Dispose();
        _lock.Dispose();
    }
}
