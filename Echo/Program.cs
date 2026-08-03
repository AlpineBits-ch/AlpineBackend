using System.Threading.RateLimiting;
using AppEnvironment;
using Echo.Docs;
using Echo.Persistence;
using Echo.Persistence.Persistance;
using Echo.Proxy;
using Echo.Realtime;
using Echo.RateLimiter;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.ServiceDiscovery.Dns;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using JasperFx.RuntimeCompiler;
using Yarp.ReverseProxy.Health;

var builder = WebApplication.CreateBuilder(args);

builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddControllers();

var redis = Env.Redis;


builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts => {});
    // The gateway must be able to SEND realtime commands (EchoRealtimeHub forwards client
    // invocations over RabbitMQ) in every environment, so we no longer short-circuit in
    // Development. Realtime therefore requires RabbitMQ + the target services to be running.
    opts.ConfigureWolverine(false);

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile bakes
    // in via `dotnet run -- codegen write` before publish.
    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        opts.Services.AddRuntimeCompilation();
    }
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);   
}



var redisConnection = await ConnectionMultiplexer.ConnectAsync($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddDataProtection()
    .SetApplicationName("yarp-proxy-cluster") // Must be the same name across all YARP instances
    .PersistKeysToStackExchangeRedis(redisConnection, "DataProtection-Keys");

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("PerUserPolicy", context =>
    {
        // Webhook execution is authenticated by a token in the path, not by a signed-in user, so
        // the usual identity partition doesn't exist for it.
        if (TryGetWebhookId(context.Request.Path, out var webhookId))
        {
            return RateLimitPartition.GetFixedWindowLimiter($"webhook:{webhookId}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
        }

        var username = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(username, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddReverseProxy()
    .LoadFromMemory(ProxyConfig.GetRoutes(), ProxyConfig.GetClusters())
    .ConfigureHttpClient((context, handler) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromSeconds(2);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2);
        handler.EnableMultipleHttp2Connections = true;
    })
    .AddConfigFilter<RateLimitConfigFilter>();
builder.Services.Configure<TransportFailureRateHealthPolicyOptions>(options =>
{
    options.DetectionWindowSize = TimeSpan.FromSeconds(10);
    options.MinimalTotalCountThreshold = 1; // fail after just 1 failure in the window
    options.DefaultFailureRateLimit = 0.3; // 30% failure rate triggers ejection
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = Env.GeneralConfiguration.InstanceUrl; 
        options.RequireHttpsMetadata = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Env.GeneralConfiguration.InstanceUrl,
            ValidateAudience = false,
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSocket clients can't set the Authorization header, so the unified hub accepts
                // the JWT via ?access_token=.
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/api/v1/ws/hub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddHttpClient();
builder.Services.AddVentaDocs();

builder.Services.AddScoped<IGitHubClient>(s =>
{
    var client = new GitHubClient(new ProductHeaderValue("AlpineUpdaterAPI"))
    {
        Credentials = new Credentials(Env.PersonalAccessToken)
    };
    return client;
});
builder.Services.AddInfrastructure();

builder.Services.AddSignalR()
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddCors(options =>
{
    options.AddPolicy("AlpinePolicy", policy =>
    {
        policy.WithOrigins("http://localhost:1420", "https://chat.alpinebits.ch", "http://tauri.localhost", "tauri://localhost", "https://app.venta.gg")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
var app = builder.Build();

app.UseCors("AlpinePolicy");
app.UseAuthentication();
app.UseAuthorization();
// The single per-user realtime connection is terminated here on the gateway.
app.MapHub<EchoRealtimeHub>("/api/v1/ws/hub");
app.MapControllers();

// Documentation site.
app.MapVentaDocs();

app.MapReverseProxy();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseInfrastructure();


await app.RunJasperFxCommands(args);

/// <summary>
/// Extracts the webhook id from "/api/webhooks/{webhookId}/{token}", the one unauthenticated write
/// route on the gateway (see the webhook-execute-route in ProxyConfig).
/// </summary>
static bool TryGetWebhookId(PathString path, out string webhookId)
{
    webhookId = string.Empty;
    if (!path.HasValue) return false;

    var value = path.Value!;
    const string prefix = "/api/webhooks/";
    if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

    var rest = value[prefix.Length..];
    var slash = rest.IndexOf('/');
    if (slash <= 0) return false;

    webhookId = rest[..slash];
    return true;
}
