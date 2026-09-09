using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class OktaSimulatorService : IOktaSimulatorService
{
    public string Issuer => "https://dev-okta-simulator.okta.com/oauth2/default";
    public string Audience => "api://unified-gateway";

    private readonly byte[] _signingKey;
    private readonly ILogger<OktaSimulatorService> _logger;

    private static readonly Dictionary<string, SimulatedUser> SimulatedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wasim.khan@gmail.com"] = new SimulatedUser
        {
            UserId = "00u_admin_wasim",
            Email = "wasim.khan@gmail.com",
            FullName = "Wasim Khan",
            Groups = new List<string> { AdGroups.Admins, AdGroups.Developers }
        },
        ["dev-user@gmail.com"] = new SimulatedUser
        {
            UserId = "00u_dev_user",
            Email = "dev-user@gmail.com",
            FullName = "Developer User",
            Groups = new List<string> { AdGroups.Developers }
        }
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public OktaSimulatorService(IOptions<GatewayOptions> options, ILogger<OktaSimulatorService> logger)
    {
        _logger = logger;
        // Derive or seed stable signing key for Okta simulator
        var secretSeed = options.Value.Security.AdminApiKey + "-okta-sim-secret-key-salt";
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(secretSeed));
    }

    public IReadOnlyList<SimulatedUser> GetSimulatedUsers()
    {
        return SimulatedUsers.Values.ToList();
    }

    public SimulatedUser? GetUserByEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        SimulatedUsers.TryGetValue(email.Trim(), out var user);
        return user;
    }

    public Task<OktaTokenResponse?> AuthenticateUserAsync(string email, string? password = null, CancellationToken cancellationToken = default)
    {
        var user = GetUserByEmail(email);
        if (user == null)
        {
            _logger.LogWarning("Simulated Okta login failed: user '{Email}' not found", email);
            return Task.FromResult<OktaTokenResponse?>(null);
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(1);

        var header = new
        {
            alg = "HS256",
            typ = "JWT",
            kid = "sim-okta-key-2026"
        };

        var payload = new OktaJwtPayload
        {
            Iss = Issuer,
            Aud = Audience,
            Sub = user.UserId,
            Email = user.Email,
            Name = user.FullName,
            PreferredUsername = user.Email,
            Groups = new List<string>(user.Groups),
            Iat = now.ToUnixTimeSeconds(),
            Nbf = now.ToUnixTimeSeconds(),
            Exp = expiresAt.ToUnixTimeSeconds(),
            Jti = $"okta_jwt_{Guid.NewGuid():N}"
        };

        var headerJson = JsonSerializer.Serialize(header, JsonOpts);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);

        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var unsignedToken = $"{headerBase64}.{payloadBase64}";
        var signatureBytes = SignHmacSha256(unsignedToken);
        var signatureBase64 = Base64UrlEncode(signatureBytes);

        var jwtToken = $"{unsignedToken}.{signatureBase64}";

        var response = new OktaTokenResponse
        {
            TokenType = "Bearer",
            ExpiresIn = 3600,
            AccessToken = jwtToken,
            IdToken = jwtToken,
            Scope = "openid profile email groups",
            User = new OktaUserProfile
            {
                UserId = user.UserId,
                Email = user.Email,
                FullName = user.FullName,
                Groups = new List<string>(user.Groups)
            }
        };

        _logger.LogInformation("Issued simulated Okta JWT for user {Email} with groups: {Groups}",
            user.Email, string.Join(", ", user.Groups));

        return Task.FromResult<OktaTokenResponse?>(response);
    }

    public (bool isValid, ClaimsPrincipal? principal, string? failureReason) ValidateOktaJwt(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, "Token is empty");

        var cleanToken = token.Trim();
        if (cleanToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            cleanToken = cleanToken[7..].Trim();

        var parts = cleanToken.Split('.');
        if (parts.Length != 3)
            return (false, null, "Malformed JWT structure: token must have 3 segments separated by dots");

        var headerSegment = parts[0];
        var payloadSegment = parts[1];
        var signatureSegment = parts[2];

        // 1. Verify Cryptographic HMAC-SHA256 Signature
        var unsignedData = $"{headerSegment}.{payloadSegment}";
        var expectedSignature = SignHmacSha256(unsignedData);

        byte[] providedSignature;
        try
        {
            providedSignature = Base64UrlDecode(signatureSegment);
        }
        catch
        {
            return (false, null, "Invalid base64url signature encoding");
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return (false, null, "Signature verification failed: token signature is invalid or has been modified");
        }

        // 2. Parse Payload
        OktaJwtPayload? payload;
        try
        {
            var payloadBytes = Base64UrlDecode(payloadSegment);
            payload = JsonSerializer.Deserialize<OktaJwtPayload>(payloadBytes, JsonOpts);
            if (payload == null)
                return (false, null, "Failed to deserialize JWT claims payload");
        }
        catch (Exception ex)
        {
            return (false, null, $"Invalid JWT payload JSON: {ex.Message}");
        }

        // 3. Validate Standard Claims
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (payload.Exp <= nowUnix)
        {
            return (false, null, $"Token has expired (exp: {payload.Exp}, current: {nowUnix})");
        }

        if (payload.Nbf > nowUnix + 30) // 30s clock skew tolerance
        {
            return (false, null, "Token is not yet active (nbf in the future)");
        }

        if (!string.Equals(payload.Iss, Issuer, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Issuer mismatch (expected: '{Issuer}', got: '{payload.Iss}')");
        }

        if (!string.Equals(payload.Aud, Audience, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Audience mismatch (expected: '{Audience}', got: '{payload.Aud}')");
        }

        // 4. Build ClaimsPrincipal with AD Group Roles
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, payload.Sub),
            new(ClaimTypes.Email, payload.Email),
            new(ClaimTypes.Name, payload.Name),
            new("preferred_username", payload.PreferredUsername),
            new("iss", payload.Iss),
            new("aud", payload.Aud),
            new("jti", payload.Jti)
        };

        foreach (var group in payload.Groups)
        {
            claims.Add(new Claim("groups", group));
            claims.Add(new Claim(ClaimTypes.Role, group));
        }

        var identity = new ClaimsIdentity(claims, "SimulatedOktaJwt");
        var principal = new ClaimsPrincipal(identity);

        return (true, principal, null);
    }

    private byte[] SignHmacSha256(string data)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace("-", "+").Replace("_", "/");
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }
}
