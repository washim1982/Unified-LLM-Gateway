using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IApplicationRegistryService
{
    Task<AppConfig?> GetAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<List<AppConfig>> GetAllAppsAsync(CancellationToken cancellationToken = default);
    Task<CreateAppResponse> CreateAppAsync(CreateAppRequest request, CancellationToken cancellationToken = default);
    Task<AppConfig?> UpdateAppAsync(string appId, UpdateAppRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<(bool isValid, AppConfig? app)> AuthenticateAppAsync(string appId, string apiKey, CancellationToken cancellationToken = default);
    Task RecordMetricAsync(RequestLogEntry log, CancellationToken cancellationToken = default);
    Task<GatewayMetricsSummary> GetMetricsSummaryAsync(CancellationToken cancellationToken = default);
}
