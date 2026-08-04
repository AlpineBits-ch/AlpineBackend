using AppEnvironment;
using Echo.Docs;
using Echo.Persistence;
using Echo.Persistence.Persistance;
using Echo.Proxy;
using Echo.Realtime;
using Echo.RateLimiter;
using Echo.Sagas;
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

var builder = WebApplication.CreateBuilder(args);

builder.AddErrorReporting();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddGracefulShutdownHealthCheck();

builder.Services.AddControllers();

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

    // Static codegen (the default) expects the ahead-of-time-generated types the Dockerfile
    // bakes in via `dotnet run -- codegen write` before publish. A local/dev/test run from raw
    // build output never runs that step, so fall back to compiling handlers on the fly.
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

// Registers the PerUserPolicy that RateLimitConfigFilter stamps onto every proxied route. The
// matching app.UseRateLimiter() call is below, after authentication - see the comment there.
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
                // WebSocket clients can't set the Authorization header, so the unified hub
                // accepts the JWT via ?access_token=. Only applies to the gateway's own hub
                // path — proxied hub traffic authenticates downstream.
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/api/v1/ws/hub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddHttpClient();
builder.Services.AddVentaDocs();

builder.Services.AddScoped<IGitHubClient>(s =>
{
    var client = new GitHubClient(new ProductHeaderValue("AlpineUpdaterAPI"))
    {
        Credentials = new Credentials(Env.PersonalAccessToken)
    };
    return client;
});
builder.Services.AddInfrastructure();

builder.Services.AddSignalR()
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");
builder.Services.AddCors(options =>
{
    options.AddPolicy("AlpinePolicy", policy =>
    {
        policy.WithOrigins("http://localhost:1420", "https://chat.alpinebits.ch", "http://tauri.localhost", "tauri://localhost", "https://app.venta.gg")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
var app = builder.Build();

app.UseCors("AlpinePolicy");
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

// The single per-user realtime connection is terminated here on the gateway. Mapped before
// the reverse proxy so YARP's catch-all routes don't swallow the hub path.
app.MapHub<EchoRealtimeHub>("/api/v1/ws/hub");
app.MapControllers();

// Documentation site. Mapped before the reverse proxy for the same reason the hub is: YARP's
// catch-all routes would otherwise swallow it. Reachable only on the docs host - there is no
// docs surface on the API host.
app.MapVentaDocs();

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
