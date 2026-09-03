using System.Text.Json.Serialization;

namespace UnifiedGateway.Models;

/// <summary>
/// A time-series data point capturing spend and token metrics for a specific time bucket.
/// </summary>
public class TimeSeriesPoint
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("spendUsd")]
    public decimal SpendUsd { get; set; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("requests")]
    public long Requests { get; set; }
}

/// <summary>
/// Detailed financial and token consumption metrics for a single registered application.
/// </summary>
public class AppBillingSummary
{
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("totalRequests")]
    public long TotalRequests { get; set; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens => InputTokens + OutputTokens;

    [JsonPropertyName("inputCostPerMillion")]
    public double InputCostPerMillion { get; set; } = 3.00;

    [JsonPropertyName("outputCostPerMillion")]
    public double OutputCostPerMillion { get; set; } = 15.00;

    [JsonPropertyName("inputCostUsd")]
    public decimal InputCostUsd { get; set; }

    [JsonPropertyName("outputCostUsd")]
    public decimal OutputCostUsd { get; set; }

    [JsonPropertyName("totalCostUsd")]
    public decimal TotalCostUsd => InputCostUsd + OutputCostUsd;

    [JsonPropertyName("costSharePercentage")]
    public double CostSharePercentage { get; set; }

    [JsonPropertyName("efficiencyRatio")]
    public double EfficiencyRatio => InputTokens > 0 ? Math.Round((double)OutputTokens / InputTokens, 3) : 0.0;

    [JsonPropertyName("dailySpendTrend")]
    public List<decimal> DailySpendTrend { get; set; } = [];

    [JsonPropertyName("lastInvokedAt")]
    public DateTimeOffset? LastInvokedAt { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Organization-wide aggregate financial report and per-application billing breakdown.
/// </summary>
public class OrganizationBillingReport
{
    [JsonPropertyName("totalSpendUsd")]
    public decimal TotalSpendUsd { get; set; }

    [JsonPropertyName("totalInputCostUsd")]
    public decimal TotalInputCostUsd { get; set; }

    [JsonPropertyName("totalOutputCostUsd")]
    public decimal TotalOutputCostUsd { get; set; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("totalInputTokens")]
    public long TotalInputTokens { get; set; }

    [JsonPropertyName("totalOutputTokens")]
    public long TotalOutputTokens { get; set; }

    [JsonPropertyName("totalRequests")]
    public long TotalRequests { get; set; }

    [JsonPropertyName("highestSpendingAppId")]
    public string HighestSpendingAppId { get; set; } = "None";

    [JsonPropertyName("highestSpendingAppName")]
    public string HighestSpendingAppName { get; set; } = "None";

    [JsonPropertyName("highestSpendingAppAmountUsd")]
    public decimal HighestSpendingAppAmountUsd { get; set; }

    [JsonPropertyName("appBills")]
    public List<AppBillingSummary> AppBills { get; set; } = [];

    [JsonPropertyName("dailySpendTrend")]
    public List<TimeSeriesPoint> DailySpendTrend { get; set; } = [];

    [JsonPropertyName("weeklySpendTrend")]
    public List<TimeSeriesPoint> WeeklySpendTrend { get; set; } = [];

    [JsonPropertyName("monthlySpendTrend")]
    public List<TimeSeriesPoint> MonthlySpendTrend { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
