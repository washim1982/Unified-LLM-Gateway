namespace UnifiedGateway.Services;

/// <summary>
/// Enterprise Cloud Storage Archival Service for S3 and WORM Audit Compliance.
/// Supports both AWS S3 in Cloud (TEST/PROD) and Local S3 Simulator (:5002).
/// </summary>
public interface IS3ArchiveService
{
    /// <summary>
    /// Checks if a bucket exists, and creates it if not.
    /// </summary>
    Task EnsureBucketExistsAsync(string bucketName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an audit log batch or file to S3 storage.
    /// </summary>
    Task<bool> UploadAuditBatchAsync(string bucketName, string key, byte[] content, string contentType = "application/json", CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an object content from S3.
    /// </summary>
    Task<string?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Health probe for the S3 backend.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
