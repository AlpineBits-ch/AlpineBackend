using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using AppEnvironment;
using Isle.Api;
using Isle.Api.Chat;
using Isle.Api.Chat.CommandController;
using Isle.Api.Repositories;
using Isle.Api.Services;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure;
using Isle.Infrastructure.Persistence;
using Isle.Infrastructure.Sfu;
using IsleBridge.Sdk;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddWolverineHttp();
builder.Services.AddSingleton<VoicePlayerRegistry>();
builder.Services.AddSingleton<VoiceTrackRegistry>();
builder.Services.AddSingleton<PlayerPresenceManager>();
builder.Services.AddSingleton<PlayerSpawnTracker>();
builder.Services.AddSingleton<CommandCooldownService>();
builder.Services.AddHostedService<PositionIngestionService>();
builder.Services.AddHostedService<PlayerJoinNotificationService>();
builder.Services.AddHostedService<GameEventIngestionService>();
builder.Services.AddHostedService<VoicePresenceReconcileService>();
builder.Services.AddHostedService<InviteTimeoutService>();
builder.Services.AddSingleton<WorldCleaner>();
builder.Services.AddHostedService<WorldCleanupService>();
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
                var accessToken = context.Request.Query["access_token"];

                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && 
                    path.StartsWithSegments("/api/v1/ws/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
          
        };
        
    });

builder.UseWolverine(opts =>
{
    if(args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();
});

// CellSize is the coarse audible-membership filter and must stay >= the client's
// attenuation radius. Proximity voice range is 80 m, so 8000 UE units (cm).
builder.Services.AddSingleton(new VoiceGridConfig { CellSize = 8000f });
builder.Services.AddSingleton<VoiceCluster>();

// Re-drives the proximity subscription graph on a short interval so a dropped/mistimed
// SubscribeMutual push converges instead of leaving one side deaf (the "I hear them, they see 0"
// asymmetry). Idempotent and symmetric — healthy subscriptions are untouched.
builder.Services.AddHostedService<VoiceSubscriptionReconcileService>();

// Cloudflare Calls SFU signalling relay for proximity voice.
builder.Services.AddScoped<CloudflareService>();
builder.Services.AddHttpClient("CloudflareProxy", client =>
{
    client.BaseAddress = new Uri($"https://rtc.live.cloudflare.com/v1/apps/{Env.CloudflareConfig.AppId}/");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", Env.CloudflareConfig.ApiToken);
});


var redis = Env.Redis;

builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddInfrastructure();


builder.Services.AddSingleton<PlayerPositionCache>();
builder.Services.AddSingleton<IPlayerPositionProvider>(sp => sp.GetRequiredService<PlayerPositionCache>());

builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var isleIpAddress = Environment.GetEnvironmentVariable("ISLE_IP_ADDRESS") ?? "10.0.0.0";

builder.Services.AddSingleton<ISkinStore, SkinStore>();
builder.Services.AddIsleBridge(cfg =>
{
    cfg.BaseAddress = new Uri($"http://{isleIpAddress}:8080");
    cfg.SlowCommandTimeout = TimeSpan.FromSeconds(10);
    cfg.EnableSkinReapply = true;
    cfg.SkinReapplyDelay = TimeSpan.FromSeconds(10);
});

var config = new EvrimaRconClientConfiguration
{
    Host     = IPAddress.Parse(isleIpAddress),
    Port     = 8888,
    Password =  Environment.GetEnvironmentVariable("RCON_PASSWORD")
}; 
using var rcon = new EvrimaRconClient(config);
await rcon.ConnectAsync();

builder.Services.AddSingleton(rcon);

builder.Services.AddSingleton<SpeciesPopulationLimits>();
builder.Services.AddHostedService<PopulationLimitService>();

builder.Services.AddHostedService<ChatWatcher>();
builder.Services.AddHostedService<PresenceService>();
builder.Services.AddHostedService<CommandController>();

if (args.Contains("codegen") || args.Contains("describe"))
{
    
    try
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
        {
            var options = new ConfigurationOptions
            {
                EndPoints = { "localhost:6379" },
                AbortOnConnectFail = false,
                AllowAdmin = false,
                Password = null 
            };

            return ConnectionMultiplexer.Connect(options);
        });   
        var codeGenApp = builder.Build();
        codeGenApp.MapWolverineEndpoints(opts =>
        {
            opts.UseDataAnnotationsValidationProblemDetailMiddleware();
        });
        await codeGenApp.RunJasperFxCommands(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"CODEGEN BUILD FAILED: {ex}");
        throw;
    }
    return;
}

var app = builder.Build();


// see initial game mode

// king of the hill 333’285.638, -331’208.952, 22’197.846

app.UseInfrastructure();

using var scope = app.Services.CreateScope();

var dbContext = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

if (!dbContext.GameModeDefinitions.Any())
{
    var gameModeDefinitions = new GameModeDefinition()
    {
        Id = GameModeDefinition.GenerateId(),
        DisplayName = "King of the Hill",
        Type = GameModeType.Casual,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        MaxDuration = TimeSpan.FromMinutes(10),
        MinParticipants = 1,
        MaxParticipants = 30,
        Cooldown = TimeSpan.FromMinutes(20),
        Enabled = true,
        Zone = new GeoFenceData()
        {
            Shape = GeoFenceShape.Circle,
            Radius = 5000,
            Center = new Vector3()
            {
                X = 333285.638f,
                Y = -331208.952f,
                Z = 22197.846f
            }
        },
        Trigger = new TriggerConfig()
        {
            MinPlayersToTrigger = 1,
            Type = TriggerType.ZoneEntry
        }
    };

    dbContext.GameModeDefinitions.Add(gameModeDefinitions);
    await dbContext.SaveChangesAsync();
}




// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseGracefulShutdownHealthCheck();
app.MapWolverineEndpoints();
app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();


await app.RunAsync();
