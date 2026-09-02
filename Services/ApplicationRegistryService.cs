using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class ApplicationRegistryService : IApplicationRegistryService
{
    private readonly ConcurrentDictionary<string, AppConfig> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RequestLogEntry> _recentLogs = new();
    private const int MaxLogHistory = 500;

    private readonly ISecurityService _securityService;
    private readonly GatewayOptions _options;
    private readonly ILogger<ApplicationRegistryService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _registryFilePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ApplicationRegistryService(
        ISecurityService securityService,
        IOptions<GatewayOptions> options,
        ILogger<ApplicationRegistryService> logger)
    {
        _securityService = securityService;
        _options = options.Value;
        _logger = logger;

        var dataDir = Path.GetFullPath(_options.Storage.DataDirectory);
        Directory.CreateDirectory(dataDir);
        _registryFilePath = Path.Combine(dataDir, _options.Storage.RegistryFileName);

        InitializeRegistry();
    }

    private void InitializeRegistry()
    {
        try
        {
            if (File.Exists(_registryFilePath))
            {
                var json = File.ReadAllText(_registryFilePath);
                var loaded = JsonSerializer.Deserialize<List<AppConfig>>(json, JsonOpts);
                if (loaded != null)
                {
                    foreach (var app in loaded)
                    {
                        _apps[app.AppId] = app;
                    }
                    _logger.LogInformation("Loaded {Count} registered applications from {Path}", _apps.Count, _registryFilePath);
                    return;
                }
            }

            SeedDefaultApps();
            PersistRegistryToFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading registry from disk, seeding defaults.");
            SeedDefaultApps();
        }
    }

    private void SeedDefaultApps()
    {
        var (key1, hash1, prefix1) = _securityService.GenerateApiKey();
        var app1 = new AppConfig
        {
            AppId = "customer-support-agent",
            Name = "Customer Support Assistant",
            Description = "Automated customer support routing and FAQ handling.",
            ApiKeyHash = hash1,
            ApiKeyPrefix = prefix1,
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20240620-v1:0",
            SystemPrompt = "You are a professional customer support agent for Acme Corp. Be concise, polite, and accurate.",
            Temperature = 0.5,
            MaxTokens = 1500,
            FallbackProvider = "local",
            FallbackModel = "ollama/llama3",
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (key2, hash2, prefix2) = _securityService.GenerateApiKey();
        var app2 = new AppConfig
        {
            AppId = "code-reviewer-pro",
            Name = "Code Reviewer Pro",
            Description = "Senior engineer automated code review and security analysis.",
            ApiKeyHash = hash2,
            ApiKeyPrefix = prefix2,
            Provider = "bedrock",
            Model = "anthropic.claude-3-5-sonnet-20240620-v1:0",
            SystemPrompt = "You are a principal staff software engineer conducting a strict, helpful code review.",
            Temperature = 0.3,
            MaxTokens = 3000,
            FallbackProvider = "local",
            FallbackModel = "ollama/llama3",
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (key3, hash3, prefix3) = _securityService.GenerateApiKey();
        var app3 = new AppConfig
        {
            AppId = "local-privacy-chat",
            Name = "Local Privacy Chat",
            Description = "Internal offline LLM for processing sensitive and confidential notes.",
            ApiKeyHash = hash3,
            ApiKeyPrefix = prefix3,
            Provider = "local",
            Model = "ollama/llama3",
            SystemPrompt = "You are a private offline assistant. No data leaves this server.",
            Temperature = 0.7,
            MaxTokens = 2048,
            FallbackProvider = "local",
            FallbackModel = "lmstudio/local-model",
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _apps[app1.AppId] = app1;
        _apps[app2.AppId] = app2;
        _apps[app3.AppId] = app3;

        _logger.LogInformation("Seeded 3 default applications with generated keys.");
    }

    public Task<AppConfig?> GetAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        _apps.TryGetValue(appId, out var app);
        return Task.FromResult(app);
    }

    public Task<List<AppConfig>> GetAllAppsAsync(CancellationToken cancellationToken = default)
    {
        var list = _apps.Values.OrderByDescending(a => a.UpdatedAt).ToList();
        return Task.FromResult(list);
    }

    public async Task<CreateAppResponse> CreateAppAsync(CreateAppRequest request, CancellationToken cancellationToken = default)
    {
        var cleanAppId = string.IsNullOrWhiteSpace(request.AppId)
            ? Guid.NewGuid().ToString("N")[..8]
            : Slugify(request.AppId);

        if (_apps.ContainsKey(cleanAppId))
        {
            throw new InvalidOperationException($"An application with AppId '{cleanAppId}' already exists.");
        }

        var (rawKey, keyHash, keyPrefix) = _securityService.GenerateApiKey();

        var app = new AppConfig
        {
            AppId = cleanAppId,
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            ApiKeyHash = keyHash,
            ApiKeyPrefix = keyPrefix,
            Provider = request.Provider,
            Model = request.Model,
            SystemPrompt = request.SystemPrompt,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            FallbackProvider = request.FallbackProvider,
            FallbackModel = request.FallbackModel,
            AllowedCidrs = request.AllowedCidrs ?? [],
            Version = 1,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            VersionHistory = []
        };

        _apps[cleanAppId] = app;
        await PersistRegistryToFileAsync();

        // Mint initial ready-to-use 1-hour STS temporary token
        var (stsToken, stsExpiresAt) = _securityService.IssueAppStsToken(cleanAppId, TimeSpan.FromHours(1), "invoke", false);

        return new CreateAppResponse
        {
            App = app,
            ApiKey = rawKey,
            EndpointUrl = $"/gateway/{cleanAppId}/invoke",
            StsToken = stsToken,
            StsExpiresAt = stsExpiresAt,
            StsDurationSeconds = 3600
        };
    }

    public async Task<AppConfig?> UpdateAppAsync(string appId, UpdateAppRequest request, CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var existing))
        {
            return null;
        }

        var snapshot = new AppConfigSnapshot
        {
            Version = existing.Version,
            Provider = existing.Provider,
            Model = existing.Model,
            SystemPrompt = existing.SystemPrompt,
            Temperature = existing.Temperature,
            MaxTokens = existing.MaxTokens,
            AllowedCidrs = existing.AllowedCidrs,
            SavedAt = existing.UpdatedAt
        };

        var history = new List<AppConfigSnapshot>(existing.VersionHistory) { snapshot };

        var updated = existing with
        {
            Name = request.Name ?? existing.Name,
            Description = request.Description ?? existing.Description,
            Provider = request.Provider ?? existing.Provider,
            Model = request.Model ?? existing.Model,
            SystemPrompt = request.SystemPrompt ?? existing.SystemPrompt,
            Temperature = request.Temperature ?? existing.Temperature,
            MaxTokens = request.MaxTokens ?? existing.MaxTokens,
            FallbackProvider = request.FallbackProvider ?? existing.FallbackProvider,
            FallbackModel = request.FallbackModel ?? existing.FallbackModel,
            AllowedCidrs = request.AllowedCidrs ?? existing.AllowedCidrs,
            IsActive = request.IsActive ?? existing.IsActive,
            Version = existing.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            VersionHistory = history
        };

        _apps[appId] = updated;
        await PersistRegistryToFileAsync();
        return updated;
    }

    public async Task<bool> DeleteAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        var removed = _apps.TryRemove(appId, out _);
        if (removed)
        {
            await PersistRegistryToFileAsync();
        }
        return removed;
    }

    public bool ValidateAppHostIp(AppConfig app, System.Net.IPAddress? clientIp)
    {
        return IpNetworkHelper.IsIpInAllowedCidrs(clientIp, app.AllowedCidrs);
    }

    public Task<(bool isValid, AppConfig? app, string? failureReason)> AuthenticateAppAsync(
        string appId, 
        string apiKey, 
        System.Net.IPAddress? clientIp = null, 
        CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var app) || !app.IsActive)
        {
            return Task.FromResult<(bool, AppConfig?, string?)>((false, null, "APP_NOT_FOUND_OR_INACTIVE"));
        }

        // Host Network Trust: verify client source IP against allowed CIDRs
        if (!ValidateAppHostIp(app, clientIp))
        {
            _logger.LogWarning("Host network access rejected for appId '{AppId}'. Client IP {ClientIp} is not in AllowedCidrs [{Cidrs}]",
                appId, clientIp, string.Join(", ", app.AllowedCidrs));
            return Task.FromResult<(bool, AppConfig?, string?)>((false, app, "HOST_IP_NOT_AUTHORIZED"));
        }

        if (!_options.Security.EnforceAppApiKey)
        {
            return Task.FromResult<(bool, AppConfig?, string?)>((true, app, null));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<(bool, AppConfig?, string?)>((false, null, "MISSING_API_KEY"));
        }

        var cleanKey = apiKey.Trim();
        if (cleanKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            cleanKey = cleanKey[7..].Trim();
        }

        // 1. Check if an Application STS Token is presented
        if (cleanKey.StartsWith("ug_sts_", StringComparison.OrdinalIgnoreCase))
        {
            var (isStsValid, payload, failureReason) = _securityService.ValidateAppStsToken(cleanKey);
            if (!isStsValid || payload == null)
            {
                _logger.LogWarning("STS token rejection for appId '{AppId}': {Reason}", appId, failureReason);
                return Task.FromResult<(bool, AppConfig?, string?)>((false, null, failureReason ?? "INVALID_STS_TOKEN"));
            }

            // Verify appId scope (Admin tokens with '*' can invoke any app; otherwise must match exact appId)
            if (!payload.IsAdmin && !string.Equals(payload.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("STS token appId mismatch. Token appId: '{TokenAppId}', Request appId: '{ReqAppId}'", payload.AppId, appId);
                return Task.FromResult<(bool, AppConfig?, string?)>((false, null, "STS_APP_ID_MISMATCH"));
            }

            return Task.FromResult<(bool, AppConfig?, string?)>((true, app, null));
        }

        // 2. Check Master Admin Key
        if (_securityService.VerifyKey(cleanKey, _securityService.HashKey(_options.Security.AdminApiKey)))
        {
            return Task.FromResult<(bool, AppConfig?, string?)>((true, app, null));
        }

        // 3. Check App Hashed Primary Long-Term API Key
        if (_securityService.VerifyKey(cleanKey, app.ApiKeyHash))
        {
            return Task.FromResult<(bool, AppConfig?, string?)>((true, app, null));
        }

        // 4. Check App Secondary Key (Dual-Key Grace Period during Rotation)
        if (!string.IsNullOrEmpty(app.SecondaryApiKeyHash))
        {
            var isSecondaryValid = _securityService.VerifyKey(cleanKey, app.SecondaryApiKeyHash);
            if (isSecondaryValid)
            {
                if (app.SecondaryKeyExpiresAt == null || DateTimeOffset.UtcNow < app.SecondaryKeyExpiresAt.Value)
                {
                    _logger.LogInformation("Authenticated app '{AppId}' using active secondary grace-period key", appId);
                    return Task.FromResult<(bool, AppConfig?, string?)>((true, app, null));
                }
                else
                {
                    _logger.LogWarning("Rejected app '{AppId}' because secondary key grace period expired at {Expiry}", appId, app.SecondaryKeyExpiresAt);
                    return Task.FromResult<(bool, AppConfig?, string?)>((false, null, "SECONDARY_KEY_EXPIRED"));
                }
            }
        }

        return Task.FromResult<(bool, AppConfig?, string?)>((false, null, "INVALID_API_KEY"));
    }

    public Task<AppStsTokenResponse?> IssueStsTokenForAppAsync(
        string? appId,
        string apiKey,
        int durationSeconds = 3600,
        string scope = "invoke",
        string? callerId = null,
        System.Net.IPAddress? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult<AppStsTokenResponse?>(null);

        var cleanKey = apiKey.Trim();
        if (cleanKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            cleanKey = cleanKey[7..].Trim();

        var duration = TimeSpan.FromSeconds(durationSeconds <= 0 ? 3600 : durationSeconds);

        // A. Is Master Admin Key provided?
        if (_securityService.VerifyKey(cleanKey, _securityService.HashKey(_options.Security.AdminApiKey)))
        {
            var targetAppId = string.IsNullOrWhiteSpace(appId) ? "*" : appId.Trim();
            var (adminToken, adminExpiresAt) = _securityService.IssueAppStsToken(
                targetAppId,
                duration,
                scope,
                isAdmin: true,
                callerId: callerId);

            return Task.FromResult<AppStsTokenResponse?>(new AppStsTokenResponse
            {
                Token = adminToken,
                TokenType = "Bearer",
                AppId = targetAppId,
                DurationSeconds = (int)duration.TotalSeconds,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = adminExpiresAt,
                Scope = scope,
                IsAdmin = true
            });
        }

        // B. Is an App Long-Term Key provided (Primary or Grace-Period Secondary)?
        AppConfig? matchedApp = null;
        if (!string.IsNullOrWhiteSpace(appId) && _apps.TryGetValue(appId, out var specificApp))
        {
            if (specificApp.IsActive)
            {
                if (_securityService.VerifyKey(cleanKey, specificApp.ApiKeyHash))
                {
                    matchedApp = specificApp;
                }
                else if (!string.IsNullOrEmpty(specificApp.SecondaryApiKeyHash) &&
                         _securityService.VerifyKey(cleanKey, specificApp.SecondaryApiKeyHash) &&
                         (specificApp.SecondaryKeyExpiresAt == null || DateTimeOffset.UtcNow < specificApp.SecondaryKeyExpiresAt.Value))
                {
                    matchedApp = specificApp;
                }
            }
        }
        else
        {
            // Search all registered apps for matching key hash
            foreach (var app in _apps.Values)
            {
                if (app.IsActive)
                {
                    if (_securityService.VerifyKey(cleanKey, app.ApiKeyHash))
                    {
                        matchedApp = app;
                        break;
                    }
                    if (!string.IsNullOrEmpty(app.SecondaryApiKeyHash) &&
                        _securityService.VerifyKey(cleanKey, app.SecondaryApiKeyHash) &&
                        (app.SecondaryKeyExpiresAt == null || DateTimeOffset.UtcNow < app.SecondaryKeyExpiresAt.Value))
                    {
                        matchedApp = app;
                        break;
                    }
                }
            }
        }

        if (matchedApp == null)
        {
            return Task.FromResult<AppStsTokenResponse?>(null);
        }

        // Host Network Trust verification before minting STS token
        if (!ValidateAppHostIp(matchedApp, clientIp))
        {
            _logger.LogWarning("STS token issuance rejected for appId '{AppId}'. Host IP {ClientIp} is not in AllowedCidrs [{Cidrs}]",
                matchedApp.AppId, clientIp, string.Join(", ", matchedApp.AllowedCidrs));
            return Task.FromResult<AppStsTokenResponse?>(null);
        }

        var (appToken, appExpiresAt) = _securityService.IssueAppStsToken(
            matchedApp.AppId,
            duration,
            scope,
            isAdmin: false,
            callerId: callerId);

        return Task.FromResult<AppStsTokenResponse?>(new AppStsTokenResponse
        {
            Token = appToken,
            TokenType = "Bearer",
            AppId = matchedApp.AppId,
            DurationSeconds = (int)duration.TotalSeconds,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = appExpiresAt,
            Scope = scope,
            IsAdmin = false
        });
    }

    public Task<AppStsTokenResponse> MintStsTokenDirectAsync(
        string appId,
        int durationSeconds = 3600,
        string scope = "invoke",
        bool isAdmin = false,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds <= 0 ? 3600 : durationSeconds);
        var (token, expiresAt) = _securityService.IssueAppStsToken(
            appId,
            duration,
            scope,
            isAdmin,
            callerId);

        return Task.FromResult(new AppStsTokenResponse
        {
            Token = token,
            TokenType = "Bearer",
            AppId = appId,
            DurationSeconds = (int)duration.TotalSeconds,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Scope = scope,
            IsAdmin = isAdmin
        });
    }

    public async Task<RotateKeyResponse?> RotateAppApiKeyAsync(
        string appId,
        int gracePeriodDays = 7,
        CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var existing))
        {
            return null;
        }

        var clampedGraceDays = Math.Max(1, Math.Min(30, gracePeriodDays <= 0 ? 7 : gracePeriodDays));
        var (newRawKey, newKeyHash, newKeyPrefix) = _securityService.GenerateApiKey();
        var now = DateTimeOffset.UtcNow;
        var secondaryExpiry = now.AddDays(clampedGraceDays);

        var updated = existing with
        {
            ApiKeyHash = newKeyHash,
            ApiKeyPrefix = newKeyPrefix,
            SecondaryApiKeyHash = existing.ApiKeyHash,
            SecondaryApiKeyPrefix = existing.ApiKeyPrefix,
            KeyRotatedAt = now,
            SecondaryKeyExpiresAt = secondaryExpiry,
            UpdatedAt = now
        };

        _apps[appId] = updated;
        await PersistRegistryToFileAsync();

        _logger.LogInformation("Rotated API key for app '{AppId}'. New prefix: {NewPrefix}, Secondary prefix: {SecPrefix}, Grace period: {Days} days",
            appId, newKeyPrefix, existing.ApiKeyPrefix, clampedGraceDays);

        return new RotateKeyResponse
        {
            AppId = appId,
            NewApiKey = newRawKey,
            NewKeyPrefix = newKeyPrefix,
            SecondaryKeyPrefix = existing.ApiKeyPrefix,
            SecondaryKeyExpiresAt = secondaryExpiry,
            RotatedAt = now
        };
    }

    public async Task<RevokeKeyResponse?> RevokeSecondaryApiKeyAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var existing))
        {
            return null;
        }

        var updated = existing with
        {
            SecondaryApiKeyHash = null,
            SecondaryApiKeyPrefix = null,
            SecondaryKeyExpiresAt = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _apps[appId] = updated;
        await PersistRegistryToFileAsync();

        _logger.LogInformation("Revoked secondary API key for app '{AppId}'", appId);

        return new RevokeKeyResponse
        {
            AppId = appId,
            Message = "Secondary API key successfully revoked.",
            RevokedAt = DateTimeOffset.UtcNow
        };
    }

    public Task RecordMetricAsync(RequestLogEntry log, CancellationToken cancellationToken = default)
    {
        _recentLogs.Enqueue(log);
        while (_recentLogs.Count > MaxLogHistory)
        {
            _recentLogs.TryDequeue(out _);
        }
        return Task.CompletedTask;
    }

    public Task<GatewayMetricsSummary> GetMetricsSummaryAsync(CancellationToken cancellationToken = default)
    {
        var logs = _recentLogs.ToArray();
        var total = logs.Length;
        var successful = logs.Count(l => l.Success);
        var failed = logs.Count(l => !l.Success);
        var fallbacks = logs.Count(l => l.FallbackUsed);
        var totalTokens = logs.Sum(l => (long)l.TotalTokens);
        var avgLatency = total > 0 ? logs.Average(l => l.LatencyMs) : 0.0;
        var bedrock = logs.Count(l => l.Provider.Equals("bedrock", StringComparison.OrdinalIgnoreCase));
        var local = logs.Count(l => l.Provider.Equals("local", StringComparison.OrdinalIgnoreCase));

        var evaluated = logs.Count(l => l.GuardrailAction != "None");
        var redacted = logs.Count(l => l.GuardrailAction == "Redacted");
        var blocked = logs.Count(l => l.GuardrailAction == "Blocked");

        var appStats = new Dictionary<string, AppMetricStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in logs.Where(l => !string.IsNullOrEmpty(l.AppId)).GroupBy(l => l.AppId!))
        {
            appStats[group.Key] = new AppMetricStats
            {
                AppId = group.Key,
                RequestCount = group.Count(),
                TokenCount = group.Sum(l => (long)l.TotalTokens),
                AvgLatencyMs = group.Average(l => l.LatencyMs),
                ErrorCount = group.Count(l => !l.Success),
                GuardrailBlockedCount = group.Count(l => l.GuardrailAction == "Blocked")
            };
        }

        var summary = new GatewayMetricsSummary
        {
            TotalRequests = total,
            SuccessfulRequests = successful,
            FailedRequests = failed,
            FallbackCount = fallbacks,
            GuardrailEvaluatedCount = evaluated,
            GuardrailRedactedCount = redacted,
            GuardrailBlockedCount = blocked,
            TotalTokens = totalTokens,
            AvgLatencyMs = Math.Round(avgLatency, 2),
            BedrockRequests = bedrock,
            LocalRequests = local,
            RecentLogs = logs.OrderByDescending(l => l.Timestamp).Take(50).ToList(),
            AppStats = appStats
        };

        return Task.FromResult(summary);
    }

    private void PersistRegistryToFile()
    {
        _fileLock.Wait();
        try
        {
            var list = _apps.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(_registryFilePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task PersistRegistryToFileAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var list = _apps.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOpts);
            await File.WriteAllTextAsync(_registryFilePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string Slugify(string text)
    {
        return text.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-")
            .Trim('-');
    }
}
