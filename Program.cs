using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Polly;
using Polly.Extensions.Http;
using UnifiedGateway.Endpoints;
using UnifiedGateway.Models;
using UnifiedGateway.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Strongly Typed Configuration
builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

var gatewayOptions = builder.Configuration
    .GetSection(GatewayOptions.SectionName)
    .Get<GatewayOptions>() ?? new GatewayOptions();

// 2. Data Protection API for secure token & key encryption
var dataProtectionKeysPath = Path.Combine(AppContext.BaseDirectory, "dataprotection-keys");
builder.Services.AddDataProtection()
    .SetApplicationName("UnifiedLLMGateway")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

// 3. Resilient HttpClientFactory for Local Providers
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .Or<TimeoutException>()
    .WaitAndRetryAsync(2, retryAttempt =>
        TimeSpan.FromMilliseconds(200 * Math.Pow(2, retryAttempt)));

builder.Services.AddHttpClient("OllamaClient", client =>
{
    client.BaseAddress = new Uri(gatewayOptions.LocalProviders.Ollama.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(gatewayOptions.LocalProviders.Ollama.TimeoutSeconds);
}).AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient("LmStudioClient", client =>
{
    client.BaseAddress = new Uri(gatewayOptions.LocalProviders.LmStudio.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(gatewayOptions.LocalProviders.LmStudio.TimeoutSeconds);
}).AddPolicyHandler(retryPolicy);

builder.Services.AddHttpClient("LlamaCppClient", client =>
{
    client.BaseAddress = new Uri(gatewayOptions.LocalProviders.LlamaCpp.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(gatewayOptions.LocalProviders.LlamaCpp.TimeoutSeconds);
}).AddPolicyHandler(retryPolicy);

// 4. Core Gateway Services Registration
builder.Services.AddSingleton<ISecurityService, SecurityService>();
builder.Services.AddSingleton<IGuardrailService, GuardrailService>();
builder.Services.AddSingleton<ISTSService, STSService>();
builder.Services.AddSingleton<IKmsCryptoService, KmsCryptoService>();
builder.Services.AddSingleton<IS3ArchiveService, S3ArchiveService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IPrometheusMetricsService, PrometheusMetricsService>();
builder.Services.AddSingleton<IBedrockService, BedrockService>();
builder.Services.AddSingleton<ILocalModelService, LocalModelService>();
builder.Services.AddSingleton<IApplicationRegistryService, ApplicationRegistryService>();
builder.Services.AddSingleton<IModelRouter, ModelRouter>();

// 5. Sliding-Window Rate Limiting (Per-Client IP & Application ID)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\":\"RATE_LIMIT_EXCEEDED\",\"message\":\"Too many requests. Please slow down.\",\"status\":429}",
            cancellationToken: token);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var appId = httpContext.Request.RouteValues.TryGetValue("appId", out var val) ? val?.ToString() : null;
        var partitionKey = string.IsNullOrEmpty(appId) ? clientIp : $"{appId}_{clientIp}";

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey,
            key => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = gatewayOptions.Security.RateLimitPerMinute > 0 ? gatewayOptions.Security.RateLimitPerMinute : 120,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});

// 6. Background Services
builder.Services.AddHostedService<AwsCredentialBackgroundService>();

// 6. CORS Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("GatewayCorsPolicy", policy =>
    {
        var allowedOrigins = gatewayOptions.Security.AllowedCorsOrigins;
        if (allowedOrigins.Contains("*"))
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

// 7. OpenAPI / Swagger Documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Universal AI LLM Gateway API",
        Version = "v1",
        Description = "Enterprise-grade Unified LLM Gateway (.NET 8) with Enterprise Guardrails (PII/PCI/Secrets), dynamic Bedrock STS assume-role, local model failover, and automated application routing."
    });

    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "Application API Key header. Format: X-API-Key: ug_live_...",
        Type = SecuritySchemeType.ApiKey,
        Name = "X-API-Key",
        In = ParameterLocation.Header
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// 7. Production Security Posture Validation
if (app.Environment.IsProduction() && (string.IsNullOrWhiteSpace(gatewayOptions.Security.AdminApiKey) || gatewayOptions.Security.AdminApiKey.Contains("dev-admin-master-key")))
{
    throw new InvalidOperationException("CRITICAL SECURITY VIOLATION: Gateway is running in Production environment with default or missing AdminApiKey. Configure Gateway:Security:AdminApiKey with a strong secret before starting in production.");
}

// 8. Middleware Pipeline
// Enterprise Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:;");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseCors("GatewayCorsPolicy");
app.UseRateLimiter();

if (app.Environment.IsDevelopment() || app.Environment.IsStaging() || app.Environment.IsEnvironment("Test"))
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Unified LLM Gateway v1");
        c.RoutePrefix = "swagger";
    });
}

// Serve embedded dashboard
app.UseDefaultFiles();
app.UseStaticFiles();

// 9. Map Minimal API Endpoints
app.MapGatewayEndpoints();
app.MapDashboardEndpoints();

// Root redirect to Dashboard
app.MapGet("/status", () => Results.Ok(new
{
    name = "Universal AI LLM Gateway",
    version = "1.0.0",
    framework = ".NET 8 Minimal API",
    guardrails = "Enabled (PII, PCI, Secrets, Prompt Injection)",
    status = "Online",
    dashboard = "/",
    swagger = "/swagger"
}));

app.Run();


