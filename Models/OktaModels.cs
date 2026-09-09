using System.Text.Json.Serialization;

namespace UnifiedGateway.Models;

public static class AdGroups
{
    public const string Admins = "UnifiedGateway-Admins";
    public const string Developers = "UnifiedGateway-Developers";
}

public class SimulatedUser
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public List<string> Groups { get; set; } = new();
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string? Password { get; set; }
}

public class OktaTokenResponse
{
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; } = 3600;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("id_token")]
    public string IdToken { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "openid profile email groups";

    [JsonPropertyName("user")]
    public OktaUserProfile User { get; set; } = new();
}

public class OktaUserProfile
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public List<string> Groups { get; set; } = new();
    public bool IsAdmin => Groups.Contains(AdGroups.Admins, StringComparer.OrdinalIgnoreCase);
    public bool IsDeveloper => Groups.Contains(AdGroups.Developers, StringComparer.OrdinalIgnoreCase);
}

public class OktaJwtPayload
{
    [JsonPropertyName("iss")]
    public string Iss { get; set; } = string.Empty;

    [JsonPropertyName("sub")]
    public string Sub { get; set; } = string.Empty;

    [JsonPropertyName("aud")]
    public string Aud { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("preferred_username")]
    public string PreferredUsername { get; set; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = new();

    [JsonPropertyName("iat")]
    public long Iat { get; set; }

    [JsonPropertyName("nbf")]
    public long Nbf { get; set; }

    [JsonPropertyName("exp")]
    public long Exp { get; set; }

    [JsonPropertyName("jti")]
    public string Jti { get; set; } = string.Empty;
}
