using System.Net;
using AppEnvironment;
using Isle.Api.Chat;
using Isle.Api.Chat.CommandController;
using Isle.Infrastructure;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using Messaging;
using StackExchange.Redis;
using TheIsleEvrimaRconClient;
using Wolverine;
using Wolverine.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
builder.Services.AddDistributedMemoryCache();

builder.UseWolverine(opts =>
{
    if(args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();
});
builder.Services.AddInfrastructure();


var redis = Env.Redis;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));

builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var isleIpAddress = Environment.GetEnvironmentVariable("ISLE_IP_ADDRESS");

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


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();


await app.RunAsync();
