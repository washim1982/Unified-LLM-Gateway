namespace UnifiedGateway.Models;

/// <summary>
/// Strongly typed root gateway configuration.
/// </summary>
public class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string Environment { get; set; } = "Development";
    public AwsOptions Aws { get; set; } = new();
    public LocalProvidersOptions LocalProviders { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public GuardrailOptions Guardrails { get; set; } = new();
}

public enum AwsAuthenticationType
{
    Direct,
    Profile,
    AssumeRole,
    RolesAnywhere
}

public class AwsOptions
{
    public string Region { get; set; } = "us-east-1";
    public string AuthenticationType { get; set; } = "Direct"; // "Profile", "AssumeRole", "RolesAnywhere", "Direct"
    public string AssumeRoleArn { get; set; } = string.Empty;
    public string RoleSessionName { get; set; } = "UnifiedGatewaySession";
    public int SessionDurationSeconds { get; set; } = 3600;
    public string LocalProfileName { get; set; } = "default";
    public bool UseLocalProfile { get; set; } = false; // Legacy fallback, alias for AuthenticationType == "Profile"
    public string? ExternalId { get; set; }
    public int RefreshBufferMinutes { get; set; } = 5;

    // AWS IAM Roles Anywhere options for On-Premises deployments (TEST & PROD)
    public RolesAnywhereOptions RolesAnywhere { get; set; } = new();

    public AwsAuthenticationType ResolvedAuthType
    {
        get
        {
            if (Enum.TryParse<AwsAuthenticationType>(AuthenticationType, true, out var type))
            {
                return type;
            }

            if (UseLocalProfile)
                return AwsAuthenticationType.Profile;

            if (!string.IsNullOrWhiteSpace(RolesAnywhere?.TrustAnchorArn) &&
                !string.IsNullOrWhiteSpace(RolesAnywhere?.ProfileArn))
            {
                return AwsAuthenticationType.RolesAnywhere;
            }

            if (!string.IsNullOrWhiteSpace(AssumeRoleArn))
                return AwsAuthenticationType.AssumeRole;

            return AwsAuthenticationType.Direct;
        }
    }
}

public class RolesAnywhereOptions
{
    public string TrustAnchorArn { get; set; } = string.Empty;
    public string ProfileArn { get; set; } = string.Empty;
    public string RoleArn { get; set; } = string.Empty;
    public string? CertificatePath { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? Passphrase { get; set; }
    public string? CertificateContent { get; set; }
    public string? PrivateKeyContent { get; set; }
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

    // KMS / HSM Configuration
    public bool EnableCloudKms { get; set; } = false;
    public string? KmsKeyId { get; set; }

    // Abuse & Safety Clamps
    public int MaxInputCharacters { get; set; } = 50_000;
    public int MaxOutputTokensClamp { get; set; } = 4_096;

    // Compliance & Audit
    public bool EnableAuditLogPersistence { get; set; } = true;
    public int AuditLogRetentionDays { get; set; } = 90;
}

public class StorageOptions
{
    public string StorageProvider { get; set; } = "LocalDisk"; // "LocalDisk", "HybridLokiS3", "S3"
    public string DataDirectory { get; set; } = "./data";
    public string RegistryFileName { get; set; } = "app_registry.json";
    public string AuditLogDirectory { get; set; } = "audit_logs";

    // AWS S3 / Glacier Archival Options
    public string? S3BucketName { get; set; }
    public string S3Prefix { get; set; } = "audit_logs/";
    public int S3GlacierTransitionDays { get; set; } = 30;
}
