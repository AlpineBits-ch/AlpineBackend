using System.Net;
using AppEnvironment;
using Isle.Api;
using Isle.Api.Chat;
using Isle.Api.Chat.CommandController;
using Isle.Api.Voice;
using Isle.Domain.Aggregates;
using Isle.Infrastructure;
using Isle.Infrastructure.Persistence;
using Isle.Infrastructure.Sfu;
using IsleBridge.Sdk;
using JasperFx;
using Messaging;
using StackExchange.Redis;
using TheIsleEvrimaRconClient;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddWolverineHttp();
builder.UseWolverine(opts =>
{
    if(args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();
});

builder.Services.AddSingleton<VoiceGridConfig>();
builder.Services.AddSingleton<VoiceCluster>();
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




builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var isleIpAddress = Environment.GetEnvironmentVariable("ISLE_IP_ADDRESS") ?? "10.0.0.0";

builder.Services.AddIsleBridge(cfg =>
{
    cfg.BaseAddress = new Uri($"http://{isleIpAddress}:8080");
    cfg.SlowCommandTimeout = TimeSpan.FromSeconds(10);
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
app.UseInfrastructure();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();


await app.RunAsync();
