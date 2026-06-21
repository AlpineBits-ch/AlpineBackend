using System.Threading.RateLimiting;
using AppEnvironment;
using Echo.Domain.Entities;
using Echo.Persistence;
using Echo.Persistence.Persistance;
using Echo.Proxy;
using Echo.RateLimiter;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.ServiceDiscovery.Dns;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Yarp.ReverseProxy.Health;

var builder = WebApplication.CreateBuilder(args);

builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddControllers();


builder.Services.AddControllers();

var redis = Env.Redis;


builder.UseWolverine(opts =>
{

    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts => {});
    if (builder.Environment.IsDevelopment())
    {
        return;
    }

    opts.ConfigureWolverine(false);
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);   
}
builder.Services.Configure<DnsServiceEndpointProviderOptions>(options =>
{
    options.DefaultRefreshPeriod = TimeSpan.FromSeconds(10);
});

builder.Services.AddServiceDiscovery()
    .AddDnsSrvServiceEndpointProvider();
var redisConnection = await ConnectionMultiplexer.ConnectAsync($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddDataProtection()
    .SetApplicationName("yarp-proxy-cluster") // Must be the same name across all YARP instances
    .PersistKeysToStackExchangeRedis(redisConnection, "DataProtection-Keys");

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("PerUserPolicy", context =>
    {
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
        handler.PooledConnectionLifetime = TimeSpan.FromSeconds(10);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10);
        handler.EnableMultipleHttp2Connections = true;
    })
    .AddServiceDiscoveryDestinationResolver()
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
    });
builder.Services.AddHttpClient();

builder.Services.AddScoped<IGitHubClient>(s =>
{
    var client = new GitHubClient(new ProductHeaderValue("AlpineUpdaterAPI"))
    {
        Credentials = new Credentials(Env.PersonalAccessToken)
    };
    return client;
});
builder.Services.AddInfrastructure();
;
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());
builder.Services.AddSignalR()
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddCors(options =>
{
    options.AddPolicy("AlpinePolicy", policy =>
    {
        policy.WithOrigins("http://localhost:1420", "https://chat.alpinebits.ch", "http://tauri.localhost", "tauri://localhost")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
var app = builder.Build();

app.UseCors("AlpinePolicy");
app.MapControllers();
app.MapReverseProxy();

app.MapHealthChecks("/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseInfrastructure();


await app.RunJasperFxCommands(args);
