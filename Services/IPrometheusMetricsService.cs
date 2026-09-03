namespace UnifiedGateway.Services;

public interface IPrometheusMetricsService
{
    void RecordRequest(string appId, string model, string provider, int statusCode, double durationSeconds);
    void RecordTokens(string appId, string model, int inputTokens, int outputTokens);
    void RecordGuardrailViolation(string category, string action, string severity);
    void RecordStsRefresh(string authType, bool success);
    void RecordHostRejection(string appId);
    string GeneratePrometheusMetricsText();
}
