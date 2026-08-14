using Echo.Auth;
using System.Net.Http.Headers;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Entitlements;
using Echo.Entitlements.Sources;
using Echo.Realtime.Sfu;
using Echo.Voice;
using AppEnvironment;
using StackExchange.Redis;
using Domain;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Infrastructure;
using Messaging.Infrastructure.Persistence;
using Messaging.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();


builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddScoped<FileService>();

// Storage entitlements, enforced by FileService.
builder.Services.AddEntitlements(builder.Configuration);
builder.Services.AddLicenseMode(
    LicenseModes.Parse(Env.License.Mode), OperatorCeilings.Parse(Env.License.OperatorCeilings));
builder.Services.AddSingleton<IGuildStorageLedger, RedisGuildStorageLedger>();

builder.Logging.SetMinimumLevel(LogLevel.Warning);

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{

    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));
builder.Services.AddSingleton<IDistributedLockService, RedisDistributedLockService>();
builder.Services.AddSingleton<LockedJsonCacheStore>();
builder.Services.AddSingleton<StreamViewerStore>();
builder.Services.AddVoiceRooms();
// Scoped: it takes IMessageBus, which Wolverine registers per scope.
builder.Services.AddScoped<DeviceIdResolver>();
builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
    }).AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts =>
    {

    });
    opts.ConfigureWolverine();

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile bakes
    // in via `dotnet run -- codegen write` before publish.
    if (builder.Environment.IsDevelopment() || !AppEnvironment.Env.MessagingConfiguration.UseScyllaDb)
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        // Dynamic mode compiles handlers with Roslyn at startup - needs an IAssemblyGenerator,
        // which core WolverineFx no longer ships (see JasperFx.RuntimeCompiler package).
        opts.Services.AddRuntimeCompilation();
    }
});
builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });
builder.Services.AddHttpClient("CloudflareRtc", client =>
{
    client.BaseAddress = new Uri("https://rtc.live.cloudflare.com/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Env.CloudflareConfig.ApiToken);
});
builder.Services.AddCloudflareCalls(Env.CloudflareConfig.AppId, Env.CloudflareConfig.ApiToken);

// Web Push, for the browser client: a named HTTP client plus the sender that resolves it.
builder.Services.AddWebPush();

builder.Services.AddScoped<ConversationPermissionService>();
builder.Services.AddScoped<MlsJoinRequestService>();
builder.Services.AddScoped<MlsDeviceCoverageService>();
builder.Services.AddScoped<MlsGroupService>();

// The §I.1 rollout knobs, read from configuration rather than baked into the binary, so the
// certificate-enforcement phase is actually flippable and two instances cannot disagree about it.
Domain.MlsPolicy.Bind(builder.Configuration);
builder.Services.AddScoped<IceServerService>();

// ── Privacy (docs/specs/privacy.md, T0-2/T0-3 and T2-18/20/22/23) ──────────────
// Both caches are Redis-backed rather than in-memory on purpose: they are read on every DM send
// and every push fan-out, and a stale per-pod copy means a user's "nobody may DM me" or their
// block silently does not apply on whichever pod serves the next request.
builder.Services.AddScoped<PrivacySettingsCache>();
builder.Services.AddScoped<BlockCache>();
builder.Services.AddScoped<DirectMessagePolicyService>();
builder.Services.AddScoped<ExplicitContentGuard>();

// T2-14's per-guild DM toggle, over Guild's GetGuildDirectMessagePreferenceRequest.
builder.Services.AddScoped<ISharedGuildDirectMessageLookup, SharedGuildDirectMessageLookup>();

// T2-20. The control exists and is honoured; classification itself is out of scope, so the default
// classifies nothing. Swapping this one registration is the whole of wiring a real scanner in.
builder.Services.AddSingleton<IMediaClassifier, NoOpMediaClassifier>();

// T2-21. There is no clip feature, and this is not one: it is the enforcement point a clip feature
// would have to pass, registered now so it cannot be shipped around later.
builder.Services.AddSingleton<IVoiceRecordingSessionConsentStore, DeniedByDefaultSessionConsentStore>();
builder.Services.AddScoped<IVoiceRecordingConsent, VoiceRecordingConsent>();

// T2-22.
builder.Services.AddSingleton(DmRetentionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<DmRetentionSweepService>();

// T0-4: telemetry consent.
builder.Services.AddTelemetryConsentGate(async (services, userIds, ct) =>
{
    var cache = services.GetRequiredService<PrivacySettingsCache>();
    var settings = await cache.GetAsync(userIds, ct);
    return settings.ToDictionary(pair => pair.Key, pair => pair.Value.AllowDataCollection);
});
if (args.Contains("codegen") || args.Contains("describe"))
{
    var debugScylla = ScyllaContext.CreateDebug();
    builder.Services.AddSingleton(debugScylla);
    var jasperApp = builder.Build();
    jasperApp.MapWolverineEndpoints();
    await jasperApp.RunJasperFxCommands(args);
    return;
}

// When USE_SCYLLA_DB=false (see Messaging.Infrastructure.MessagingInfrastructure), message/
// reaction storage goes through Postgres/EF Core instead, so don't open a real Cassandra
// connection here - it would otherwise require a live Scylla node to even boot regardless of
// that flag (the e2e test harness relies on this to avoid provisioning a Scylla container).
var scylla = Env.MessagingConfiguration.UseScyllaDb
    ? await ScyllaContext.CreateAsync()
    : ScyllaContext.CreateDebug();
builder.Services.AddSingleton(scylla);

builder.Services.AddVentaJwtBearer(webSocketPath: "/api/v1/ws/hubs");

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
var app = builder.Build();
app.UseInfrastructure();

// Configure the HTTP request pipeline.
app.MapOpenApi("/internal/openapi/{documentName}.json");


app.UseHttpsRedirection();

app.MapControllers();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/messaging/health");

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);

