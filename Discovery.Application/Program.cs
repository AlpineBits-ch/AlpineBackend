using AppEnvironment;
using Billing.Contracts.Clients;
using Discovery.Api.Bus;
using Discovery.Api.Services;
using Discovery.Infrastructure;
using Discovery.Infrastructure.Persistence;
using Echo.Auth;
using Echo.Entitlements;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Sources;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
builder.Services.AddScoped<ListingWriteService>();
builder.Services.AddScoped<GuildProfileMirror>();
builder.Services.AddScoped<DiscoveryFeedQuery>();

// The public-listing plan gate, and what backs it.
builder.Services.AddEntitlements(builder.Configuration);
builder.Services.AddLicenseMode(
    LicenseModes.Parse(Env.License.Mode), OperatorCeilings.Parse(Env.License.OperatorCeilings));

// Billing-backed sources, hosted only - self-host runs on SelfHostEverythingSource alone.
if (Env.License.IsHosted && Env.License.IsBillingConfigured)
{
    builder.Services.AddBillingGrantSource();
    builder.Services.AddBillingPlanSource();
}

// The cache in front of the resolver above.
builder.Services.AddEntitlementCache();

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddSignalR(config => { config.EnableDetailedErrors = true; })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddWolverineHttp();
builder.Services.AddVentaJwtBearer();
builder.Services.AddHostedService<GameCatalogSyncService>();

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
