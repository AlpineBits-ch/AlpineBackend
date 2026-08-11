using Echo.Auth;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Realtime.Sfu;
using Echo.Voice;
using System.Net.Http.Headers;
using AppEnvironment;
using Facet.Dashboard;
using Guild.Application.Services;
using Guild.Persistence;
using Guild.Persistence.Persistence;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Social.Contracts.Services;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;


var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();
builder.Services.AddWolverineHttp()
    .ConfigureHttpJsonOptions(options =>
    {
        
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    });



builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var redis = Env.Redis;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}"));
builder.Services.AddSingleton<IDistributedLockService, RedisDistributedLockService>();
builder.Services.AddSingleton<LockedJsonCacheStore>();
builder.Services.AddSingleton<StreamViewerStore>();
builder.Services.AddVoiceRooms();
builder.Services.AddSingleton<GuildVoiceActivityStore>();
// Scoped: it takes IMessageBus, which Wolverine registers per scope.
builder.Services.AddScoped<DeviceIdResolver>();

builder.Services.AddScoped<ProfileService>();
builder.Services.AddStackExchangeRedisCache(config =>
{
    
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddSignalR(config =>
    {
        config.EnableDetailedErrors = true;
   }).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
})
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = Env.Redis.ConnectionString;
});
builder.UseWolverine(opts =>
{
    if(args.Contains("facets")) return;
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


builder.Services.AddVentaJwtBearer(webSocketPath: "/api/v1/ws/hubs");

builder.Services.AddFacetDashboard();
builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddScoped<GuildHydrateService>();
builder.Services.AddScoped<GuildPermissionService>();

// Backs ChannelAudienceService's per-(channel,user) memo, which keeps the realtime handlers'
// new ViewChannel filtering off the per-message hot path.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ChannelAudienceService>();
builder.Services.AddScoped<GuildThumbnailService>();
builder.Services.AddScoped<GuildEmojiService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<OnboardingValidationService>();
builder.Services.AddScoped<OnboardingGrantService>();
builder.Services.AddScoped<OnboardingConfigService>();
builder.Services.AddScoped<ForumService>();
builder.Services.AddScoped<HouseholdChannelService>();
builder.Services.AddScoped<HouseholdNotifier>();
builder.Services.AddScoped<HouseholdAlertService>();
builder.Services.AddScoped<ChoreReminderService>();
builder.Services.AddScoped<PantryRestockService>();
builder.Services.AddScoped<PantryExpiryService>();
builder.Services.AddScoped<PantryCaptureService>();
builder.Services.AddScoped<ProductCatalogService>();
builder.Services.AddScoped<ProductCatalogSearchService>();
builder.Services.AddScoped<ProductCatalogImportService>();
builder.Services.AddScoped<ProductCatalogFillService>();
// Singletons, and both for the same reason: they hold the one thing that must not be per-request.
builder.Services.AddSingleton<ProductCatalogRateLimiter>();
builder.Services.AddSingleton<ProductCatalogLookupService>();
builder.Services.AddScoped<AbsenceService>();
builder.Services.AddScoped<LedgerSummaryService>();
builder.Services.AddScoped<PaymentHandleService>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddScoped<MaintenanceAlertService>();
builder.Services.AddScoped<ChoreAlertService>();
builder.Services.AddScoped<BillAlertService>();
builder.Services.AddScoped<BillService>();
builder.Services.AddScoped<MealAlertService>();
builder.Services.AddScoped<MealPlanService>();
builder.Services.AddScoped<HouseholdDigestService>();
builder.Services.AddScoped<InboxTaskService>();
builder.Services.AddScoped<ChoreRotationService>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<MoveOutService>();
builder.Services.AddScoped<HomeStatusService>();
builder.Services.AddScoped<NotificationResolutionService>();
// Scoped, not singleton: both take IMessageBus, which Wolverine registers per scope.
builder.Services.AddScoped<PrivacySettingsCache>();
builder.Services.AddScoped<BlockCache>();
builder.Services.AddScoped<GuildDirectMessagePreferenceService>();
builder.Services.AddScoped<InboxService>();
builder.Services.AddScoped<InboxMentionService>();

// T0-4: telemetry consent.
builder.Services.AddTelemetryConsentGate(async (services, userIds, ct) =>
{
    var cache = services.GetRequiredService<PrivacySettingsCache>();
    var settings = await cache.GetAsync(userIds, ct);
    return settings.ToDictionary(pair => pair.Key, pair => pair.Value.AllowDataCollection);
});

builder.Services.AddHostedService<VoiceHeartbeatCleanupService>();
builder.Services.AddHostedService<HouseholdReconcileService>();
builder.Services.AddHostedService<ForumAutoArchiveService>();
builder.Services.AddCloudflareCalls(Env.CloudflareConfig.AppId, Env.CloudflareConfig.ApiToken);

// The one outbound client the pantry has.
builder.Services.AddHttpClient(ProductCatalogLookupService.HttpClientName, client =>
{
    client.BaseAddress = new Uri(Env.ProductCatalog.ApiBaseUrl);

    // The outer bound only.
    client.Timeout = Env.ProductCatalog.RequestTimeout;

    // TryAddWithoutValidation rather than ParseAdd: the source asks for a specific User-Agent shape
    // and an operator who mistypes it should get a request the source may throttle, not a service
    // that fails to start.
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Env.ProductCatalog.UserAgent);
});

// A second client for the bulk exports, because the one above is tuned for the opposite job.
builder.Services.AddHttpClient(ProductCatalogAutoImportService.HttpClientName, client =>
{
    // Generous, and it bounds the whole streamed download rather than a request.
    client.Timeout = TimeSpan.FromHours(1);

    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Env.ProductCatalog.UserAgent);
});

// One instance, resolved twice.
builder.Services.AddSingleton<ProductCatalogAutoImportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProductCatalogAutoImportService>());
if (args.Contains("codegen") || args.Contains("describe"))
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
    codeGenApp.MapWolverineEndpoints();
    codeGenApp.MapFacetDashboard();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);
}

if (args.Contains("facets"))
{
    builder.Services.AddFacetDashboard();
    builder.Services.AddDbContext<MicroserviceContext>();
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
    var facetApp = builder.Build();
    facetApp.MapFacetDashboard();
    await facetApp.RunAsync();
    Environment.Exit(0);   
}

var app = builder.Build();

app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/guild/health");

// Configure the HTTP request pipeline.
app.MapOpenApi("/internal/openapi/{documentName}.json");

app.UseHttpsRedirection();
app.UseInfrastructure();
app.MapControllers();
app.MapFacetDashboard();


await app.RunAsync();
