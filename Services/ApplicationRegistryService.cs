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

            // Seed initial default apps
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
            Model = "meta.llama3-70b-instruct-v1:0",
            SystemPrompt = "You are a Principal Software Architect. Review code for correctness, security, and performance.",
            Temperature = 0.2,
            MaxTokens = 3000,
            FallbackProvider = "bedrock",
            FallbackModel = "anthropic.claude-3-haiku-20240307-v1:0",
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
            Version = 1,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            VersionHistory = []
        };

        _apps[cleanAppId] = app;
        await PersistRegistryToFileAsync();

        return new CreateAppResponse
        {
            App = app,
            ApiKey = rawKey,
            EndpointUrl = $"/gateway/{cleanAppId}/invoke"
        };
    }

    public async Task<AppConfig?> UpdateAppAsync(string appId, UpdateAppRequest request, CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var existing))
        {
            return null;
        }

        // Create snapshot of previous state for version history
        var snapshot = new AppConfigSnapshot
        {
            Version = existing.Version,
            Provider = existing.Provider,
            Model = existing.Model,
            SystemPrompt = existing.SystemPrompt,
            Temperature = existing.Temperature,
            MaxTokens = existing.MaxTokens,
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

    public Task<(bool isValid, AppConfig? app)> AuthenticateAppAsync(string appId, string apiKey, CancellationToken cancellationToken = default)
    {
        if (!_apps.TryGetValue(appId, out var app) || !app.IsActive)
        {
            return Task.FromResult<(bool, AppConfig?)>((false, null));
        }

        if (!_options.Security.EnforceAppApiKey)
        {
            return Task.FromResult<(bool, AppConfig?)>((true, app));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<(bool, AppConfig?)>((false, null));
        }

        // Master admin key bypass
        if (_securityService.VerifyKey(apiKey, _securityService.HashKey(_options.Security.AdminApiKey)))
        {
            return Task.FromResult<(bool, AppConfig?)>((true, app));
        }

        var isValid = _securityService.VerifyKey(apiKey, app.ApiKeyHash);
        return Task.FromResult<(bool, AppConfig?)>((isValid, isValid ? app : null));
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

        var appStats = new Dictionary<string, AppMetricStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in logs.Where(l => !string.IsNullOrEmpty(l.AppId)).GroupBy(l => l.AppId!))
        {
            appStats[group.Key] = new AppMetricStats
            {
                AppId = group.Key,
                RequestCount = group.Count(),
                TokenCount = group.Sum(l => (long)l.TotalTokens),
                AvgLatencyMs = group.Average(l => l.LatencyMs),
                ErrorCount = group.Count(l => !l.Success)
            };
        }

        var summary = new GatewayMetricsSummary
        {
            TotalRequests = total,
            SuccessfulRequests = successful,
            FailedRequests = failed,
            FallbackCount = fallbacks,
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
