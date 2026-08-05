using AppEnvironment;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Social.Api.Services;
using Social.Infrastructure;
using Social.Infrastructure.Persistence;
using Social.Infrastructure.Seed;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();

builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddInfrastructure();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddScoped<FileService>();

// Privacy enforcement (docs/specs/privacy.md).
builder.Services.AddScoped<PrivacySettingsCache>();
builder.Services.AddScoped<ProfileProjectionService>();
builder.Services.AddScoped<ISharedGuildResolver, BusSharedGuildResolver>();
builder.Services.AddScoped<IIdentityProfileFactsResolver, BusIdentityProfileFactsResolver>();
builder.Services.AddScoped<UserDirectory>();

// Game catalog.
builder.Services.AddScoped<GameCatalogSeeder>();
builder.Services.AddScoped<GameCatalogLookup>();
builder.Services.AddHostedService<GameCatalogSeedService>();

// The only thing standing between an unauthenticated local IPC socket and every server the user
// is in. See ActivityWriteGuard's docblock.
builder.Services.AddScoped<ActivityWriteGuard>();

// T0-4: telemetry consent.
builder.Services.AddTelemetryConsentGate(async (services, userIds, ct) =>
{
    var cache = services.GetRequiredService<PrivacySettingsCache>();
    var settings = await cache.GetManyAsync(userIds, ct);
    return settings.ToDictionary(pair => pair.Key, pair => pair.Value.AllowDataCollection);
});

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});


// Social pushes relationship-lifecycle events (social.*) at the two users involved through
// IHubContext<EchoRealtimeHub>.
builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddWolverineHttp();
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

builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile bakes
    // in via `dotnet run -- codegen write` before publish.
    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        // Dynamic mode compiles handlers with Roslyn at startup - needs an IAssemblyGenerator,
        // which core WolverineFx no longer ships (see JasperFx.RuntimeCompiler package).
        opts.Services.AddRuntimeCompilation();
    }
});
if (args.Contains("codegen") || args.Contains("describe"))
{
    
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);
}
var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseHttpsRedirection();
app.MapControllers();

app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/social/health");
app.UseInfrastructure();

await app.RunJasperFxCommands(args);

