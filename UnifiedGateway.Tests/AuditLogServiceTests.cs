using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class AuditLogServiceTests
{
    [Fact]
    public async Task AuditLogService_LogsAndQueriesRecords_AndExportsCsv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ug-audit-tests-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new GatewayOptions
        {
            Security = new SecurityOptions
            {
                EnableAuditLogPersistence = true
            },
            Storage = new StorageOptions
            {
                DataDirectory = tempDir,
                AuditLogDirectory = "audit_logs"
            }
        });

        await using var auditService = new AuditLogService(options, NullLogger<AuditLogService>.Instance);

        var record1 = new AuditLogRecord
        {
            AuditId = "aud_01",
            Timestamp = DateTimeOffset.UtcNow,
            AppId = "finance-app",
            CallerId = "user_42",
            AuthType = "StsToken",
            AuthKeyPrefix = "ug_sts_ey",
            Route = "/gateway/finance-app/invoke",
            Model = "claude-3-5-sonnet",
            Provider = "bedrock",
            InputGuardrailAction = "Passed",
            OutputGuardrailAction = "Redacted",
            GuardrailViolations = ["Output:Secrets:AwsAccessKeyId"],
            InputTokens = 120,
            OutputTokens = 45,
            LatencyMs = 350,
            StatusCode = 200,
            Success = true
        };

        var record2 = new AuditLogRecord
        {
            AuditId = "aud_02",
            Timestamp = DateTimeOffset.UtcNow,
            AppId = "hr-app",
            CallerId = "user_99",
            AuthType = "PermanentKey",
            Route = "/gateway/hr-app/invoke",
            Model = "llama3",
            Provider = "local",
            InputGuardrailAction = "Blocked",
            OutputGuardrailAction = "None",
            GuardrailViolations = ["Input:PromptInjection:Jailbreak"],
            InputTokens = 50,
            OutputTokens = 0,
            LatencyMs = 20,
            StatusCode = 422,
            Success = false,
            ErrorMessage = "Prompt injection detected"
        };

        auditService.LogRequest(record1);
        auditService.LogRequest(record2);

        // Allow async background writer to flush channel
        await Task.Delay(250);

        // 1. Query all
        var queryAll = await auditService.QueryLogsAsync(new AuditLogQueryRequest());
        Assert.Equal(2, queryAll.TotalCount);

        // 2. Query filtered by AppId
        var queryFinance = await auditService.QueryLogsAsync(new AuditLogQueryRequest { AppId = "finance-app" });
        Assert.Single(queryFinance.Records);
        Assert.Equal("finance-app", queryFinance.Records[0].AppId);
        Assert.Equal("Redacted", queryFinance.Records[0].OutputGuardrailAction);

        // 3. Query filtered by status "blocked"
        var queryBlocked = await auditService.QueryLogsAsync(new AuditLogQueryRequest { Status = "blocked" });
        Assert.Single(queryBlocked.Records);
        Assert.Equal("hr-app", queryBlocked.Records[0].AppId);

        // 4. Export CSV
        var csv = await auditService.ExportLogsCsvAsync(new AuditLogQueryRequest());
        Assert.Contains("AuditId,Timestamp,AppId", csv);
        Assert.Contains("finance-app", csv);
        Assert.Contains("hr-app", csv);
        Assert.Contains("Prompt injection detected", csv);
    }
}
