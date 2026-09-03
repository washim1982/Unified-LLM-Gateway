using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace UnifiedGateway.Services;

public class PrometheusMetricsService : IPrometheusMetricsService
{
    private static readonly double[] LatencyBuckets =
    {
        0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0, 30.0, 60.0
    };

    private readonly ConcurrentDictionary<string, long> _requestCounters = new();
    private readonly ConcurrentDictionary<string, long> _tokenCounters = new();
    private readonly ConcurrentDictionary<string, long> _guardrailCounters = new();
    private readonly ConcurrentDictionary<string, long> _stsCounters = new();
    private readonly ConcurrentDictionary<string, long> _hostRejectionCounters = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<double, long>> _histogramBuckets = new();
    private readonly ConcurrentDictionary<string, double> _histogramSums = new();
    private readonly ConcurrentDictionary<string, long> _histogramCounts = new();

    public void RecordRequest(string appId, string model, string provider, int statusCode, double durationSeconds)
    {
        var safeApp = SanitizeLabel(appId);
        var safeModel = SanitizeLabel(model);
        var safeProvider = SanitizeLabel(provider);
        var key = $"{safeApp}|{safeModel}|{safeProvider}|{statusCode}";

        _requestCounters.AddOrUpdate(key, 1, (_, current) => current + 1);

        // Record Histogram
        var histKey = $"{safeApp}|{safeModel}";
        var buckets = _histogramBuckets.GetOrAdd(histKey, _ => new ConcurrentDictionary<double, long>());

        foreach (var bucket in LatencyBuckets)
        {
            if (durationSeconds <= bucket)
            {
                buckets.AddOrUpdate(bucket, 1, (_, cur) => cur + 1);
            }
        }
        // +Inf bucket
        buckets.AddOrUpdate(double.PositiveInfinity, 1, (_, cur) => cur + 1);

        _histogramSums.AddOrUpdate(histKey, durationSeconds, (_, cur) => cur + durationSeconds);
        _histogramCounts.AddOrUpdate(histKey, 1, (_, cur) => cur + 1);
    }

    public void RecordTokens(string appId, string model, int inputTokens, int outputTokens)
    {
        var safeApp = SanitizeLabel(appId);
        var safeModel = SanitizeLabel(model);

        if (inputTokens > 0)
        {
            var inKey = $"{safeApp}|{safeModel}|input";
            _tokenCounters.AddOrUpdate(inKey, inputTokens, (_, current) => current + inputTokens);
        }

        if (outputTokens > 0)
        {
            var outKey = $"{safeApp}|{safeModel}|output";
            _tokenCounters.AddOrUpdate(outKey, outputTokens, (_, current) => current + outputTokens);
        }
    }

    public void RecordGuardrailViolation(string category, string action, string severity)
    {
        var safeCat = SanitizeLabel(category);
        var safeAct = SanitizeLabel(action);
        var safeSev = SanitizeLabel(severity);
        var key = $"{safeCat}|{safeAct}|{safeSev}";

        _guardrailCounters.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    public void RecordStsRefresh(string authType, bool success)
    {
        var safeAuth = SanitizeLabel(authType);
        var status = success ? "success" : "failure";
        var key = $"{safeAuth}|{status}";

        _stsCounters.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    public void RecordHostRejection(string appId)
    {
        var safeApp = SanitizeLabel(appId);
        _hostRejectionCounters.AddOrUpdate(safeApp, 1, (_, current) => current + 1);
    }

    public string GeneratePrometheusMetricsText()
    {
        var sb = new StringBuilder();

        // 1. Requests Total
        sb.AppendLine("# HELP gateway_requests_total Total number of processed LLM gateway requests.");
        sb.AppendLine("# TYPE gateway_requests_total counter");
        foreach (var (key, value) in _requestCounters)
        {
            var parts = key.Split('|');
            if (parts.Length == 4)
            {
                sb.AppendLine($"gateway_requests_total{{app_id=\"{parts[0]}\",model=\"{parts[1]}\",provider=\"{parts[2]}\",status=\"{parts[3]}\"}} {value}");
            }
        }
        sb.AppendLine();

        // 2. Tokens Total
        sb.AppendLine("# HELP gateway_tokens_total Total tokens processed across LLM models and applications.");
        sb.AppendLine("# TYPE gateway_tokens_total counter");
        foreach (var (key, value) in _tokenCounters)
        {
            var parts = key.Split('|');
            if (parts.Length == 3)
            {
                sb.AppendLine($"gateway_tokens_total{{app_id=\"{parts[0]}\",model=\"{parts[1]}\",token_type=\"{parts[2]}\"}} {value}");
            }
        }
        sb.AppendLine();

        // 3. Request Latency Histogram
        sb.AppendLine("# HELP gateway_request_duration_seconds Execution latency for model invocations in seconds.");
        sb.AppendLine("# TYPE gateway_request_duration_seconds histogram");
        foreach (var (histKey, buckets) in _histogramBuckets)
        {
            var parts = histKey.Split('|');
            var appId = parts.Length > 0 ? parts[0] : "unknown";
            var model = parts.Length > 1 ? parts[1] : "unknown";

            foreach (var le in LatencyBuckets)
            {
                buckets.TryGetValue(le, out var bucketCount);
                sb.AppendLine($"gateway_request_duration_seconds_bucket{{app_id=\"{appId}\",model=\"{model}\",le=\"{le.ToString(CultureInfo.InvariantCulture)}\"}} {bucketCount}");
            }

            buckets.TryGetValue(double.PositiveInfinity, out var infCount);
            sb.AppendLine($"gateway_request_duration_seconds_bucket{{app_id=\"{appId}\",model=\"{model}\",le=\"+Inf\"}} {infCount}");

            _histogramSums.TryGetValue(histKey, out var sum);
            _histogramCounts.TryGetValue(histKey, out var count);

            sb.AppendLine($"gateway_request_duration_seconds_sum{{app_id=\"{appId}\",model=\"{model}\"}} {sum.ToString("F4", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"gateway_request_duration_seconds_count{{app_id=\"{appId}\",model=\"{model}\"}} {count}");
        }
        sb.AppendLine();

        // 4. Guardrail Violations
        sb.AppendLine("# HELP gateway_guardrail_violations_total Total security, PII, and prompt safety violations detected.");
        sb.AppendLine("# TYPE gateway_guardrail_violations_total counter");
        foreach (var (key, value) in _guardrailCounters)
        {
            var parts = key.Split('|');
            if (parts.Length == 3)
            {
                sb.AppendLine($"gateway_guardrail_violations_total{{category=\"{parts[0]}\",action=\"{parts[1]}\",severity=\"{parts[2]}\"}} {value}");
            }
        }
        sb.AppendLine();

        // 5. STS Refresh Attempts
        sb.AppendLine("# HELP gateway_sts_refreshes_total Total AWS STS credential refresh attempts.");
        sb.AppendLine("# TYPE gateway_sts_refreshes_total counter");
        foreach (var (key, value) in _stsCounters)
        {
            var parts = key.Split('|');
            if (parts.Length == 2)
            {
                sb.AppendLine($"gateway_sts_refreshes_total{{auth_type=\"{parts[0]}\",status=\"{parts[1]}\"}} {value}");
            }
        }
        sb.AppendLine();

        // 6. Host Network Rejections
        sb.AppendLine("# HELP gateway_host_rejections_total Total requests rejected due to source IP / CIDR whitelisting mismatch.");
        sb.AppendLine("# TYPE gateway_host_rejections_total counter");
        foreach (var (appId, value) in _hostRejectionCounters)
        {
            sb.AppendLine($"gateway_host_rejections_total{{app_id=\"{appId}\"}} {value}");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static string SanitizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        return value
            .Replace("\"", "\\\"")
            .Replace("\n", "")
            .Replace("\r", "")
            .Trim();
    }
}
