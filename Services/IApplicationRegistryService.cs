using System.Net;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IApplicationRegistryService
{
    Task<AppConfig?> GetAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<List<AppConfig>> GetAllAppsAsync(CancellationToken cancellationToken = default);
    Task<CreateAppResponse> CreateAppAsync(CreateAppRequest request, CancellationToken cancellationToken = default);
    Task<AppConfig?> UpdateAppAsync(string appId, UpdateAppRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<(bool isValid, AppConfig? app, string? failureReason)> AuthenticateAppAsync(string appId, string apiKey, IPAddress? clientIp = null, CancellationToken cancellationToken = default);
    Task<AppStsTokenResponse?> IssueStsTokenForAppAsync(string? appId, string apiKey, int durationSeconds = 3600, string scope = "invoke", string? callerId = null, IPAddress? clientIp = null, CancellationToken cancellationToken = default);
    Task<AppStsTokenResponse> MintStsTokenDirectAsync(string appId, int durationSeconds = 3600, string scope = "invoke", bool isAdmin = false, string? callerId = null, CancellationToken cancellationToken = default);

    // Host Network Trust
    bool ValidateAppHostIp(AppConfig app, IPAddress? clientIp);

    // Key Rotation Operations
    Task<RotateKeyResponse?> RotateAppApiKeyAsync(string appId, int gracePeriodDays = 7, CancellationToken cancellationToken = default);
    Task<RevokeKeyResponse?> RevokeSecondaryApiKeyAsync(string appId, CancellationToken cancellationToken = default);

    Task RecordMetricAsync(RequestLogEntry log, CancellationToken cancellationToken = default);
    Task<GatewayMetricsSummary> GetMetricsSummaryAsync(CancellationToken cancellationToken = default);
}
