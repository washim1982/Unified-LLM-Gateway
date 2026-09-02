using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IAuditLogService
{
    void LogRequest(AuditLogRecord record);
    Task<AuditLogQueryResult> QueryLogsAsync(AuditLogQueryRequest request, CancellationToken cancellationToken = default);
    Task<string> ExportLogsCsvAsync(AuditLogQueryRequest request, CancellationToken cancellationToken = default);
}
