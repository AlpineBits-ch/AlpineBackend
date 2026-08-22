using AppEnvironment;
using Discovery.Api;
using Discovery.Api.Bus;
using Discovery.Api.Services;
using Discovery.Infrastructure;
using Discovery.Infrastructure.Persistence;
using Echo.Auth;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using System.Text.Json.Serialization;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

builder.Services.AddOpenApi();
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddInfrastructure();
builder.Services.AddMemoryCache();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<TopicResolver>();
builder.Services.AddScoped<InterestService>();
builder.Services.AddScoped<ListingRealtime>();
builder.Services.AddScoped<DiscoveryBanService>();
builder.Services.AddScoped<ListingWriteService>();
builder.Services.AddScoped<GuildProfileMirror>();
builder.Services.AddScoped<DiscoveryFeedQuery>();

builder.Services.AddDiscoveryEntitlements(builder.Configuration);

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

// Distinct from the cache above: the game catalog sync lease needs SET NX / compare-and-delete,
// which IDistributedCache does not expose.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));

builder.Services.AddSignalR(config => { config.EnableDetailedErrors = true; })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddWolverineHttp();
builder.Services.AddVentaJwtBearer();
// Singleton, not just a hosted service: GameCatalogChangedHandler injects it directly to reuse the
// same lease-guarded, chunk-committing sync instead of duplicating it.
builder.Services.AddSingleton<GameCatalogSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameCatalogSyncService>());

builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(_ => { });
    opts.ConfigureWolverine();

    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        opts.Services.AddRuntimeCompilation();
    }
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    return await codeGenApp.RunJasperFxCommands(args);
}

var app = builder.Build();

app.MapOpenApi("/internal/openapi/{documentName}.json");
app.UseHttpsRedirection();
app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();
app.MapHealthChecks("/discovery/health");
app.UseInfrastructure();

return await app.RunJasperFxCommands(args);
