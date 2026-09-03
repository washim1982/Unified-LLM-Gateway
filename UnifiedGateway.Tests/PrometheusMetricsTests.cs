using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UnifiedGateway.Models;
using UnifiedGateway.Services;
using Xunit;

namespace UnifiedGateway.Tests;

public class PrometheusMetricsTests
{
    [Fact]
    public void RecordRequest_IncrementsCountersAndHistogramCorrectly()
    {
        var service = new PrometheusMetricsService();

        service.RecordRequest("app-alpha", "claude-3-5-sonnet", "bedrock", 200, 0.45);
        service.RecordRequest("app-alpha", "claude-3-5-sonnet", "bedrock", 200, 1.20);
        service.RecordRequest("app-beta", "llama3", "local", 500, 0.05);

        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("gateway_requests_total{app_id=\"app-alpha\",model=\"claude-3-5-sonnet\",provider=\"bedrock\",status=\"200\"} 2", output);
        Assert.Contains("gateway_requests_total{app_id=\"app-beta\",model=\"llama3\",provider=\"local\",status=\"500\"} 1", output);
        Assert.Contains("gateway_request_duration_seconds_bucket{app_id=\"app-alpha\",model=\"claude-3-5-sonnet\",le=\"0.5\"} 1", output);
        Assert.Contains("gateway_request_duration_seconds_bucket{app_id=\"app-alpha\",model=\"claude-3-5-sonnet\",le=\"2.5\"} 2", output);
        Assert.Contains("gateway_request_duration_seconds_count{app_id=\"app-alpha\",model=\"claude-3-5-sonnet\"} 2", output);
    }

    [Fact]
    public void RecordTokens_TracksInputAndOutputTokens()
    {
        var service = new PrometheusMetricsService();

        service.RecordTokens("test-app", "claude-3-5-sonnet", 1500, 300);
        service.RecordTokens("test-app", "claude-3-5-sonnet", 500, 200);

        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("gateway_tokens_total{app_id=\"test-app\",model=\"claude-3-5-sonnet\",token_type=\"input\"} 2000", output);
        Assert.Contains("gateway_tokens_total{app_id=\"test-app\",model=\"claude-3-5-sonnet\",token_type=\"output\"} 500", output);
    }

    [Fact]
    public void RecordGuardrailViolation_IncrementsViolationCounts()
    {
        var service = new PrometheusMetricsService();

        service.RecordGuardrailViolation("pci_dss", "Redacted", "high");
        service.RecordGuardrailViolation("pci_dss", "Redacted", "high");
        service.RecordGuardrailViolation("prompt_injection", "Blocked", "critical");

        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("gateway_guardrail_violations_total{category=\"pci_dss\",action=\"Redacted\",severity=\"high\"} 2", output);
        Assert.Contains("gateway_guardrail_violations_total{category=\"prompt_injection\",action=\"Blocked\",severity=\"critical\"} 1", output);
    }

    [Fact]
    public void RecordStsRefresh_TracksRefreshAttempts()
    {
        var service = new PrometheusMetricsService();

        service.RecordStsRefresh("RolesAnywhere", true);
        service.RecordStsRefresh("RolesAnywhere", true);
        service.RecordStsRefresh("Profile", false);

        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("gateway_sts_refreshes_total{auth_type=\"RolesAnywhere\",status=\"success\"} 2", output);
        Assert.Contains("gateway_sts_refreshes_total{auth_type=\"Profile\",status=\"failure\"} 1", output);
    }

    [Fact]
    public void RecordHostRejection_TracksRejectionCounts()
    {
        var service = new PrometheusMetricsService();

        service.RecordHostRejection("finance-app");
        service.RecordHostRejection("finance-app");
        service.RecordHostRejection("hr-app");

        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("gateway_host_rejections_total{app_id=\"finance-app\"} 2", output);
        Assert.Contains("gateway_host_rejections_total{app_id=\"hr-app\"} 1", output);
    }

    [Fact]
    public void GeneratePrometheusMetricsText_ContainsOfficialPrometheusHeaders()
    {
        var service = new PrometheusMetricsService();
        var output = service.GeneratePrometheusMetricsText();

        Assert.Contains("# HELP gateway_requests_total", output);
        Assert.Contains("# TYPE gateway_requests_total counter", output);
        Assert.Contains("# HELP gateway_tokens_total", output);
        Assert.Contains("# TYPE gateway_tokens_total counter", output);
        Assert.Contains("# HELP gateway_request_duration_seconds", output);
        Assert.Contains("# TYPE gateway_request_duration_seconds histogram", output);
        Assert.Contains("# HELP gateway_guardrail_violations_total", output);
        Assert.Contains("# TYPE gateway_guardrail_violations_total counter", output);
        Assert.Contains("# HELP gateway_sts_refreshes_total", output);
        Assert.Contains("# TYPE gateway_sts_refreshes_total counter", output);
        Assert.Contains("# HELP gateway_host_rejections_total", output);
        Assert.Contains("# TYPE gateway_host_rejections_total counter", output);
    }
}
