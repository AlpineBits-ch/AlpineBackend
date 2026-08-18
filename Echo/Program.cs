using Echo.Auth;
using AppEnvironment;
using Billing.Contracts.Clients;
using Echo.Billing;
using Echo.Cors;
using Echo.Docs;
using Echo.Dtos.Entitlements;
using Echo.Entitlements;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Echo.Moderation;
using Echo.Persistence;
using Echo.Persistence.Persistance;
using Echo.Proxy;
using Echo.Realtime;
using Echo.RateLimiter;
using Echo.Sagas;
using Echo.Sites;
using Echo.Status;
using Echo.Wiki;
using JasperFx;
using Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.ServiceDiscovery.Dns;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using JasperFx.RuntimeCompiler;
using Yarp.ReverseProxy.Health;

// Before anything else, because the alternative to failing here is failing invisibly: a hosted
// instance with no billing configuration resolves every entitlement to unlimited and looks healthy
// while giving the product away.
Env.License.EnsureConfigured();

var builder = WebApplication.CreateBuilder(args);

builder.AddErrorReporting();

// Add services to the container.
builder.Services.AddOpenApi();

// Enums cross the wire as their names, not as integers.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddGracefulShutdownHealthCheck();

var redis = Env.Redis;


builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(opts => {});
    // The gateway must be able to SEND realtime commands (EchoRealtimeHub forwards client
    // invocations over RabbitMQ) in every environment, so we no longer short-circuit in
    // Development. Realtime therefore requires RabbitMQ + the target services to be running.
    opts.ConfigureWolverine(false);

    // Nothing may be handled while Wolverine is still starting: a saga chain compiled on a
    // listener thread clears the same handler list StartAsync is enumerating, and the host dies
    // before it is ever healthy. See WolverineListenerStartup for the full mechanism.
    opts.DeferListenerStartup();

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile bakes
    // in via `dotnet run -- codegen write` before publish.
    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        opts.Services.AddRuntimeCompilation();
    }
});

// Registered immediately after UseWolverine so the host runs it directly after Wolverine's own
// runtime has finished starting - hosted services start in registration order, and that ordering
// is the entire guarantee here (see WolverineListenerStartup).
builder.Services.AddHostedService<DeferredWolverineListeners>();

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    await codeGenApp.RunJasperFxCommands(args);
    Environment.Exit(0);   
}



var redisConnection = await ConnectionMultiplexer.ConnectAsync($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddDataProtection()
    .SetApplicationName("yarp-proxy-cluster") // Must be the same name across all YARP instances
    .PersistKeysToStackExchangeRedis(redisConnection, "DataProtection-Keys");

// Registers the PerUserPolicy that RateLimitConfigFilter stamps onto every proxied route.
builder.Services.AddEchoRateLimiting();

builder.Services.AddReverseProxy()
    .LoadFromMemory(ProxyConfig.GetRoutes(), ProxyConfig.GetClusters())
    .ConfigureHttpClient((context, handler) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromSeconds(2);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2);
        handler.EnableMultipleHttp2Connections = true;
    })
    .AddConfigFilter<RateLimitConfigFilter>();
builder.Services.Configure<TransportFailureRateHealthPolicyOptions>(options =>
{
    options.DetectionWindowSize = TimeSpan.FromSeconds(10);
    options.MinimalTotalCountThreshold = 1; // fail after just 1 failure in the window
    options.DefaultFailureRateLimit = 0.3; // 30% failure rate triggers ejection
});
// WebSocket clients can't set the Authorization header, so the unified hub accepts the JWT via
// ?access_token=. Only the gateway's own hub path - proxied hub traffic authenticates downstream.
builder.Services.AddVentaJwtBearer(webSocketPath: "/api/v1/ws/hub");
builder.Services.AddHttpClient();
builder.Services.AddVentaDocs();

// Moderation console and support site.
builder.Services.AddScoped<StaffAccess>();
builder.Services.AddSingleton<ModerationMailer>();
builder.Services.AddScoped<EmailService>();

// The status page: the request counters, the detector, the public snapshot, and the CORS and
// rate-limit policies that belong to it alone.
builder.Services.AddVentaStatus();

// The public wiki site: its channel to the Guild service and its own per-IP budget.
builder.Services.AddVentaWiki();

// Entitlements, read side.
builder.Services.AddEntitlements(builder.Configuration);
builder.Services.AddLicenseMode(
    LicenseModes.Parse(Env.License.Mode),
    OperatorCeilings.Parse(Env.License.OperatorCeilings));
builder.Services.AddSingleton(new EntitlementInstanceInfo(
    Env.License.Mode.Trim().ToLowerInvariant(),
    Env.License.IsHosted && Env.License.IsBillingConfigured,
    // Handed to the client rather than bundled into it: Alpine points at arbitrary instances at
    // runtime, so a key compiled into the build would aim a self-hoster's checkout at ours.
    string.IsNullOrWhiteSpace(Env.License.StripePublishableKey)
        ? null
        : Env.License.StripePublishableKey.Trim()));

// The resolved-set cache backs onto the same Redis everything else here uses.
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

// Only when Billing is actually deployed.
if (Env.License.IsHosted && Env.License.IsBillingConfigured)
{
    builder.Services.AddBillingGrantSource();

    // And the plans themselves, from the service that owns them.
    builder.Services.AddBillingPlanSource();
    builder.Services.AddBillingEntitlementVersions();
}
else
{
    builder.Services.AddSingleton<IEntitlementVersionProvider, StaticEntitlementVersionProvider>();
}

// After the sources, because the cache fingerprints its Redis keys from them so that two services
// resolving different answers cannot read each other's entries.
var entitlementCache = new EntitlementCacheOptions();
builder.Services.AddEntitlementCache(entitlementCache);
builder.Services.AddEntitlementVersionCache();
builder.Services.AddSingleton(new EntitlementReadOptions { TtlSeconds = entitlementCache.ClientTtlSeconds });
builder.Services.AddSingleton<EntitlementSnapshotBuilder>();
builder.Services.AddSingleton<EntitlementsChangeNotifier>();

// The console's billing section talks to Billing over HTTP rather than over the bus, because what
// it needs is the staff surface - grants, plans, blast radius, the audit trail - and that surface
// is HTTP with its own authorization on it.
builder.Services.AddHttpClient<BillingServiceClient>(client =>
{
    // Parsed rather than constructed: BILLING_SERVICE_URL is operator-supplied, and a typo in it on
    // a selfhost instance - where nothing here is ever called - should not stop the gateway booting.
    // An unset base address surfaces as the same 503 an unreachable service does.
    client.BaseAddress = Uri.TryCreate(BillingServiceClient.BaseAddress, UriKind.Absolute, out var billingBase)
        ? billingBase
        : null;

    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<IGitHubClient>(s =>
{
    var client = new GitHubClient(new ProductHeaderValue("AlpineUpdaterAPI"))
    {
        Credentials = new Credentials(Env.PersonalAccessToken)
    };
    return client;
});
builder.Services.AddInfrastructure();

// This is the only process that terminates a client socket (see MapHub below), so it is the only
// one that sets timeouts - and RealtimeHubTimeouts is where the two numbers and the reasoning
// behind them live.
builder.Services.AddSignalR(RealtimeHubTimeouts.Configure)
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
// Cross-origin sign-in for partner sites, scoped to the OIDC protocol endpoints and attached to
// individual proxy routes rather than to a path prefix.
builder.Services.AddSsoCors();

// The first-party client allowlist: desktop webview origins, a dev server, and the browser web
// client.
builder.Services.AddAlpineCors();

var app = builder.Build();

// Before anything can be refused: a CORS problem leaves no server-side trace, so the resolved
// allowlist is stated in the log rather than left to be inferred from a browser console.
app.LogAlpineCorsOrigins();

// First in the pipeline so it wraps everything, including the proxy and the gateway's own
// controllers.
app.UseStatusMetrics();

app.UseCors(AlpineCors.PolicyName);
app.UseAuthentication();
app.UseAuthorization();

// Placed here on purpose, and the placement is the whole feature:
//
//   * after routing - WebApplication injects UseRouting ahead of every middleware registered here,
//     so the selected endpoint (and with it the RateLimiterPolicy metadata RateLimitConfigFilter
//     stamps onto each YARP route) is already resolvable. Without that the limiter finds no policy
//     metadata and lets everything through, which is the bug this is fixing.
//   * after UseAuthentication - the partitioner reads the caller's subject claim off
//     HttpContext.User. Run any earlier and User is still anonymous, so every signed-in caller on
//     the instance would be lumped into the shared address bucket.
//   * before the endpoint terminal - MapHub/MapControllers/MapReverseProxy below only register
//     endpoints; the endpoint middleware that executes them is appended after everything here, so
//     a rejection still short-circuits before any proxying or hub negotiation happens.
//
// Only endpoints that carry the policy metadata are limited: there is no global limiter, so the
// realtime hub, /health and the gateway's own controllers are unaffected.
app.UseEchoRateLimiter();

// The single per-user realtime connection is terminated here on the gateway.
app.MapHub<EchoRealtimeHub>("/api/v1/ws/hub");
app.MapControllers();

// Documentation site.
app.MapVentaDocs();

// The moderation console, the support site and the status page.
app.MapVentaSites();

// Published guild wikis, on wiki.<instance>. Mapped after the other sites so its host guard is the
// innermost of the site branches.
app.MapVentaWiki();

app.MapReverseProxy();
app.UseGracefulShutdownHealthCheck();

app.MapHealthChecks("/health");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseInfrastructure();


await app.RunJasperFxCommands(args);
