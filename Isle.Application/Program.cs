using Echo.Auth;
using AppEnvironment;
using Echo.Realtime.LiveKit;
using Isle.Api.Extensions;
using Isle.Infrastructure;
using Isle.Infrastructure.Persistence;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
var redis = Env.Redis;

builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddLogging();
builder.Services.AddOpenApi();
builder.Services.AddWolverineHttp();

builder.Services.AddVentaJwtBearer(webSocketPath: "/api/v1/ws/hubs");

builder.UseWolverine(opts =>
{
    if (args.Contains("facets")) return;
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(_ => { });
    opts.ConfigureWolverine();

    // After the shared config: this overrides what that sets up for the damage feed.
    opts.ConfigureIsleMessaging();
});

builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddInfrastructure();

// The LiveKit control plane for proximity voice.
builder.Services.AddLiveKit();

builder.Services.AddIsleApplication();

if (args.Contains("codegen") || args.Contains("describe"))
{
    int exitCode;
    try
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { "localhost:6379" },
                AbortOnConnectFail = false,
                AllowAdmin = false,
                Password = null,
            }));

        var codeGenApp = builder.Build();
        codeGenApp.MapWolverineEndpoints(opts =>
        {
            opts.UseDataAnnotationsValidationProblemDetailMiddleware();
        });
        // JasperFx catches a codegen failure itself and reports it as an exit code, so discarding
        // this is how a half-generated image gets built and shipped as a success.
        exitCode = await codeGenApp.RunJasperFxCommands(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"CODEGEN BUILD FAILED: {ex}");
        throw;
    }

    return exitCode;
}

var app = builder.Build();

app.UseInfrastructure();

await app.SeedGameModeDefinitionsAsync();
await app.SeedQuestsAsync();

// Configure the HTTP request pipeline.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseGracefulShutdownHealthCheck();
app.MapWolverineEndpoints();
app.MapHealthChecks("/isle/health");
app.UseHttpsRedirection();

await app.RunAsync();

return 0;
