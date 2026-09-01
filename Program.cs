using Microsoft.AspNetCore.DataProtection;
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
builder.Services.AddSingleton<ISTSService, STSService>();
builder.Services.AddSingleton<IBedrockService, BedrockService>();
builder.Services.AddSingleton<ILocalModelService, LocalModelService>();
builder.Services.AddSingleton<IApplicationRegistryService, ApplicationRegistryService>();
builder.Services.AddSingleton<IModelRouter, ModelRouter>();

// 5. Credential Auto-Refresh Background Service
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
        Description = "Enterprise-grade Unified LLM Gateway (.NET 8) with dynamic Bedrock STS assume-role, local model failover, and automated application routing."
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

// 8. Middleware Pipeline
app.UseCors("GatewayCorsPolicy");

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
    status = "Online",
    dashboard = "/",
    swagger = "/swagger"
}));

app.Run();
