using System.Text.Json.Serialization;

namespace UnifiedGateway.Models;

/// <summary>
/// Immutable structured audit log record persisted to daily append-only JSONL files.
/// </summary>
public record AuditLogRecord
{
    [JsonPropertyName("auditId")]
    public string AuditId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    [JsonPropertyName("callerId")]
    public string? CallerId { get; init; }

    [JsonPropertyName("authType")]
    public string AuthType { get; init; } = "PermanentKey"; // "PermanentKey", "StsToken", "MasterAdminKey", "None"

    [JsonPropertyName("authKeyPrefix")]
    public string? AuthKeyPrefix { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("route")]
    public string Route { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("inputGuardrailAction")]
    public string InputGuardrailAction { get; init; } = "None"; // "Passed", "Redacted", "Blocked", "Audited", "None"

    [JsonPropertyName("outputGuardrailAction")]
    public string OutputGuardrailAction { get; init; } = "None"; // "Passed", "Redacted", "Blocked", "Audited", "None"

    [JsonPropertyName("guardrailViolations")]
    public List<string> GuardrailViolations { get; init; } = [];

    [JsonPropertyName("inputTokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public int OutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens => InputTokens + OutputTokens;

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; } = 200;

    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("fallbackUsed")]
    public bool FallbackUsed { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Query filter request for auditing compliance records.
/// </summary>
public record AuditLogQueryRequest
{
    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    [JsonPropertyName("fromDate")]
    public DateTimeOffset? FromDate { get; init; }

    [JsonPropertyName("toDate")]
    public DateTimeOffset? ToDate { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; } // "success", "error", "blocked"

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 100;

    [JsonPropertyName("skip")]
    public int Skip { get; init; } = 0;
}

/// <summary>
/// Query result for audit records.
/// </summary>
public record AuditLogQueryResult
{
    [JsonPropertyName("records")]
    public List<AuditLogRecord> Records { get; init; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("logFilesCount")]
    public int LogFilesCount { get; init; }
}
