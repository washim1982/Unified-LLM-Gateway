using System.Text.Json.Serialization;

namespace UnifiedGateway.Models;

/// <summary>
/// Application registration configuration and routing metadata.
/// </summary>
public record AppConfig
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    // Primary Active Key
    [JsonPropertyName("apiKeyHash")]
    public string ApiKeyHash { get; set; } = string.Empty;

    [JsonPropertyName("apiKeyPrefix")]
    public string ApiKeyPrefix { get; set; } = string.Empty;

    // Dual-Key Rotation Support (Secondary Grace-Period Key)
    [JsonPropertyName("secondaryApiKeyHash")]
    public string? SecondaryApiKeyHash { get; set; }

    [JsonPropertyName("secondaryApiKeyPrefix")]
    public string? SecondaryApiKeyPrefix { get; set; }

    [JsonPropertyName("keyRotatedAt")]
    public DateTimeOffset? KeyRotatedAt { get; set; }

    [JsonPropertyName("secondaryKeyExpiresAt")]
    public DateTimeOffset? SecondaryKeyExpiresAt { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "bedrock"; // "bedrock" or "local"

    [JsonPropertyName("model")]
    public string Model { get; init; } = "anthropic.claude-3-5-sonnet-20240620-v1:0";

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; init; } = "You are a helpful AI assistant.";

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; } = 2048;

    [JsonPropertyName("fallbackProvider")]
    public string? FallbackProvider { get; init; } // e.g. "local"

    [JsonPropertyName("fallbackModel")]
    public string? FallbackModel { get; init; } // e.g. "llama3"

    // Host Network Trust (Source IP / CIDR Whitelisting)
    [JsonPropertyName("allowedCidrs")]
    public List<string> AllowedCidrs { get; init; } = [];

    // Financial & Token Cost Configuration ($ per Million Tokens)
    [JsonPropertyName("inputCostPerMillion")]
    public double InputCostPerMillion { get; init; } = 3.00;

    [JsonPropertyName("outputCostPerMillion")]
    public double OutputCostPerMillion { get; init; } = 15.00;

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("versionHistory")]
    public List<AppConfigSnapshot> VersionHistory { get; init; } = [];
}

public record AppConfigSnapshot
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("allowedCidrs")]
    public List<string> AllowedCidrs { get; init; } = [];

    [JsonPropertyName("inputCostPerMillion")]
    public double InputCostPerMillion { get; init; } = 3.00;

    [JsonPropertyName("outputCostPerMillion")]
    public double OutputCostPerMillion { get; init; } = 15.00;

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; }
}

public record CreateAppRequest
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "bedrock";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "anthropic.claude-3-5-sonnet-20240620-v1:0";

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; init; } = "You are a helpful AI assistant.";

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; } = 2048;

    [JsonPropertyName("fallbackProvider")]
    public string? FallbackProvider { get; init; }

    [JsonPropertyName("fallbackModel")]
    public string? FallbackModel { get; init; }

    [JsonPropertyName("allowedCidrs")]
    public List<string> AllowedCidrs { get; init; } = [];

    [JsonPropertyName("inputCostPerMillion")]
    public double InputCostPerMillion { get; init; } = 3.00;

    [JsonPropertyName("outputCostPerMillion")]
    public double OutputCostPerMillion { get; init; } = 15.00;
}

public record CreateAppResponse
{
    [JsonPropertyName("app")]
    public AppConfig App { get; init; } = null!;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("endpointUrl")]
    public string EndpointUrl { get; init; } = string.Empty;

    [JsonPropertyName("stsToken")]
    public string StsToken { get; init; } = string.Empty;

    [JsonPropertyName("stsExpiresAt")]
    public DateTimeOffset StsExpiresAt { get; init; }

    [JsonPropertyName("stsDurationSeconds")]
    public int StsDurationSeconds { get; init; } = 3600;
}

public record UpdateAppRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("fallbackProvider")]
    public string? FallbackProvider { get; init; }

    [JsonPropertyName("fallbackModel")]
    public string? FallbackModel { get; init; }

    [JsonPropertyName("allowedCidrs")]
    public List<string>? AllowedCidrs { get; init; }

    [JsonPropertyName("inputCostPerMillion")]
    public double? InputCostPerMillion { get; init; }

    [JsonPropertyName("outputCostPerMillion")]
    public double? OutputCostPerMillion { get; init; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; init; }
}

public record RotateKeyRequest
{
    [JsonPropertyName("gracePeriodDays")]
    public int GracePeriodDays { get; init; } = 7; // Default: 7-day dual key grace period
}

public record RotateKeyResponse
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("newApiKey")]
    public string NewApiKey { get; init; } = string.Empty;

    [JsonPropertyName("newKeyPrefix")]
    public string NewKeyPrefix { get; init; } = string.Empty;

    [JsonPropertyName("secondaryKeyPrefix")]
    public string? SecondaryKeyPrefix { get; init; }

    [JsonPropertyName("secondaryKeyExpiresAt")]
    public DateTimeOffset? SecondaryKeyExpiresAt { get; init; }

    [JsonPropertyName("rotatedAt")]
    public DateTimeOffset RotatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record RevokeKeyResponse
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset RevokedAt { get; init; } = DateTimeOffset.UtcNow;
}
