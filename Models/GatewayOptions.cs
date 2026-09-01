namespace UnifiedGateway.Models;

/// <summary>
/// Strongly typed root gateway configuration.
/// </summary>
public class GatewayOptions
{
    public const string SectionName = "Gateway";

    public AwsOptions Aws { get; set; } = new();
    public LocalProvidersOptions LocalProviders { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public GuardrailOptions Guardrails { get; set; } = new();
}

public class AwsOptions
{
    public string Region { get; set; } = "us-east-1";
    public string AssumeRoleArn { get; set; } = string.Empty;
    public string RoleSessionName { get; set; } = "UnifiedGatewaySession";
    public int SessionDurationSeconds { get; set; } = 3600;
    public string LocalProfileName { get; set; } = "default";
    public bool UseLocalProfile { get; set; } = false;
    public string? ExternalId { get; set; }
    public int RefreshBufferMinutes { get; set; } = 5;
}

public class LocalProvidersOptions
{
    public OllamaOptions Ollama { get; set; } = new();
    public LmStudioOptions LmStudio { get; set; } = new();
    public LlamaCppOptions LlamaCpp { get; set; } = new();
}

public class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
}

public class LmStudioOptions
{
    public string BaseUrl { get; set; } = "http://localhost:1234";
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
}

public class LlamaCppOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
}

public class SecurityOptions
{
    public string AdminApiKey { get; set; } = "ug-admin-default-change-in-prod";
    public bool EnforceAppApiKey { get; set; } = true;
    public int RateLimitPerMinute { get; set; } = 120;
    public string[] AllowedCorsOrigins { get; set; } = ["*"];
}

public class StorageOptions
{
    public string DataDirectory { get; set; } = "./data";
    public string RegistryFileName { get; set; } = "app_registry.json";
}
