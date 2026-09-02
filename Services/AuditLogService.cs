using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class AuditLogService : IAuditLogService, IAsyncDisposable
{
    private readonly Channel<AuditLogRecord> _channel;
    private readonly string _auditLogDir;
    private readonly bool _persistenceEnabled;
    private readonly ILogger<AuditLogService> _logger;
    private readonly Task _processorTask;
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public AuditLogService(
        IOptions<GatewayOptions> options,
        ILogger<AuditLogService> logger)
    {
        _logger = logger;
        var gatewayOpts = options.Value;
        _persistenceEnabled = gatewayOpts.Security.EnableAuditLogPersistence;

        var baseDir = Path.GetFullPath(gatewayOpts.Storage.DataDirectory);
        _auditLogDir = Path.Combine(baseDir, gatewayOpts.Storage.AuditLogDirectory);

        if (_persistenceEnabled)
        {
            Directory.CreateDirectory(_auditLogDir);
        }

        _channel = Channel.CreateUnbounded<AuditLogRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _processorTask = Task.Run(ProcessAuditChannelAsync);
    }

    public void LogRequest(AuditLogRecord record)
    {
        if (!_persistenceEnabled)
            return;

        _channel.Writer.TryWrite(record);
    }

    private async Task ProcessAuditChannelAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var record))
            {
                try
                {
                    var dateStr = record.Timestamp.UtcDateTime.ToString("yyyyMMdd");
                    var filePath = Path.Combine(_auditLogDir, $"audit_{dateStr}.jsonl");
                    var line = JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine;

                    await File.AppendAllTextAsync(filePath, line, Encoding.UTF8, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Failed to persist audit log entry {AuditId}", record.AuditId);
                }
            }
        }
    }

    public async Task<AuditLogQueryResult> QueryLogsAsync(AuditLogQueryRequest request, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_auditLogDir))
        {
            return new AuditLogQueryResult { Records = [], TotalCount = 0, LogFilesCount = 0 };
        }

        var files = Directory.GetFiles(_auditLogDir, "audit_*.jsonl")
            .OrderByDescending(f => f)
            .ToList();

        var matchedRecords = new List<AuditLogRecord>();

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            foreach (var line in lines.Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<AuditLogRecord>(line, JsonOpts);
                    if (record == null) continue;

                    // Filter: AppId
                    if (!string.IsNullOrWhiteSpace(request.AppId) &&
                        !string.Equals(record.AppId, request.AppId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Filter: FromDate
                    if (request.FromDate.HasValue && record.Timestamp < request.FromDate.Value)
                    {
                        continue;
                    }

                    // Filter: ToDate
                    if (request.ToDate.HasValue && record.Timestamp > request.ToDate.Value)
                    {
                        continue;
                    }

                    // Filter: Status
                    if (!string.IsNullOrWhiteSpace(request.Status))
                    {
                        var reqStatus = request.Status.ToLowerInvariant();
                        if (reqStatus == "success" && !record.Success) continue;
                        if (reqStatus == "error" && record.Success) continue;
                        if (reqStatus == "blocked" && record.InputGuardrailAction != "Blocked" && record.OutputGuardrailAction != "Blocked") continue;
                    }

                    matchedRecords.Add(record);
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }

        var totalCount = matchedRecords.Count;
        var paged = matchedRecords
            .Skip(request.Skip)
            .Take(Math.Max(1, Math.Min(500, request.Limit)))
            .ToList();

        return new AuditLogQueryResult
        {
            Records = paged,
            TotalCount = totalCount,
            LogFilesCount = files.Count
        };
    }

    public async Task<string> ExportLogsCsvAsync(AuditLogQueryRequest request, CancellationToken cancellationToken = default)
    {
        // Query up to 5,000 records for export
        var query = request with { Limit = 5000, Skip = 0 };
        var result = await QueryLogsAsync(query, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("AuditId,Timestamp,AppId,CallerId,AuthType,KeyPrefix,ClientIp,Route,Model,Provider,InputGuardrails,OutputGuardrails,TokensTotal,LatencyMs,StatusCode,Success,ErrorMessage");

        foreach (var r in result.Records)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(r.AuditId),
                EscapeCsv(r.Timestamp.ToString("o")),
                EscapeCsv(r.AppId ?? "direct"),
                EscapeCsv(r.CallerId ?? "-"),
                EscapeCsv(r.AuthType),
                EscapeCsv(r.AuthKeyPrefix ?? "-"),
                EscapeCsv(r.ClientIp ?? "-"),
                EscapeCsv(r.Route),
                EscapeCsv(r.Model),
                EscapeCsv(r.Provider),
                EscapeCsv(r.InputGuardrailAction),
                EscapeCsv(r.OutputGuardrailAction),
                r.TotalTokens,
                r.LatencyMs,
                r.StatusCode,
                r.Success ? "true" : "false",
                EscapeCsv(r.ErrorMessage ?? "")
            ));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();

        try
        {
            await _processorTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation on dispose
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
