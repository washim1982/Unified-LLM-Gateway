using System.Security.Cryptography;
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
    private readonly GatewayOptions _options;
    private readonly IS3ArchiveService _s3ArchiveService;
    private readonly ILogger<AuditLogService> _logger;
    private readonly Task _processorTask;
    private readonly CancellationTokenSource _cts = new();
    private string _lastEntryHash = "GENESIS";
    private readonly object _hashLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public AuditLogService(
        IOptions<GatewayOptions> options,
        IS3ArchiveService s3ArchiveService,
        ILogger<AuditLogService> logger)
    {
        _logger = logger;
        _options = options.Value;
        _s3ArchiveService = s3ArchiveService;
        _persistenceEnabled = _options.Security.EnableAuditLogPersistence;

        var baseDir = Path.GetFullPath(_options.Storage.DataDirectory);
        _auditLogDir = Path.Combine(baseDir, _options.Storage.AuditLogDirectory);

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

        // Apply Cryptographic HMAC-SHA256 Hash Chaining (WORM Compliance)
        if (_options.Security.EnableTamperEvidentLogging)
        {
            lock (_hashLock)
            {
                var prev = _lastEntryHash;
                var currentHash = ComputeEntryHash(prev, record);
                record = record with { PreviousHash = prev, EntryHash = currentHash };
                _lastEntryHash = currentHash;
            }
        }

        _channel.Writer.TryWrite(record);
    }

    private string ComputeEntryHash(string previousHash, AuditLogRecord r)
    {
        var rawData = $"{previousHash}|{r.Timestamp:o}|{r.AppId}|{r.Route}|{r.StatusCode}|{r.TotalTokens}|{r.LatencyMs}|{string.Join(",", r.GuardrailViolations)}";
        var key = Encoding.UTF8.GetBytes(_options.Security.AdminApiKey);
        using var hmac = new HMACSHA256(key);
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task ProcessAuditChannelAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;
        var batchCount = 0;
        var lastS3Upload = DateTimeOffset.UtcNow;

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
                    batchCount++;

                    // S3 Archival: Flush on every 5 records or every 15 seconds
                    var elapsed = DateTimeOffset.UtcNow - lastS3Upload;
                    if (batchCount >= 5 || elapsed > TimeSpan.FromSeconds(15))
                    {
                        await ArchiveDailyLogToS3Async(filePath, dateStr, token).ConfigureAwait(false);
                        batchCount = 0;
                        lastS3Upload = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Failed to persist audit log entry {AuditId}", record.AuditId);
                }
            }
        }
    }

    private async Task ArchiveDailyLogToS3Async(string filePath, string dateStr, CancellationToken token)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var bucket = !string.IsNullOrWhiteSpace(_options.Security.S3AuditBucket)
                ? _options.Security.S3AuditBucket
                : "unified-gateway-audit-logs";

            var fileBytes = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
            var s3Key = $"audit_logs/{dateStr.Substring(0, 4)}/{dateStr.Substring(4, 2)}/audit_{dateStr}.jsonl";

            await _s3ArchiveService.UploadAuditBatchAsync(bucket, s3Key, fileBytes, "application/x-ndjson", token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background S3 archival encounter warning for audit date {Date}", dateStr);
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
        sb.AppendLine("AuditId,Timestamp,AppId,CallerId,AuthType,KeyPrefix,ClientIp,Route,Model,Provider,InputGuardrails,OutputGuardrails,TokensTotal,LatencyMs,StatusCode,Success,PreviousHash,EntryHash,ErrorMessage");

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
                EscapeCsv(r.PreviousHash),
                EscapeCsv(r.EntryHash),
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
