using System.Net;
using System.Security.Claims;
using Echo.RateLimiter;
using Echo.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Echo.Tests.RateLimiting;

/// <summary>
/// A real ASP.NET Core pipeline, built the same way the gateway builds its own: the production <see
/// cref="GatewayRateLimiting.AddEchoRateLimiting"/> policy, authentication ahead of <see
/// cref="GatewayRateLimiting.UseEchoRateLimiter"/>, and endpoints carrying the very same <see
/// cref="GatewayRateLimiting.PolicyName"/> metadata that <see cref="RateLimitConfigFilter"/> stamps
/// onto YARP's routes.
/// </summary>
public sealed class GatewayRateLimitHarness : IAsyncDisposable
{
    /// <summary>Header the stand-in authentication middleware reads a subject claim from.</summary>
    public const string SubjectHeader = "X-Test-Subject";

    /// <summary>A proxied route, exactly as ProxyConfig shapes them.</summary>
    public const string ProxiedPath = "/api/v1/guild/channels";

    /// <summary>A second proxied route, to prove the bucket is per-caller and not per-route.</summary>
    public const string OtherProxiedPath = "/api/v1/messaging/messages";

    /// <summary>An endpoint with no policy metadata, standing in for /health and the hub.</summary>
    public const string UnlimitedPath = "/health";

    /// <summary>Reports which of the proxy-secret headers survived the gateway pipeline.</summary>
    public const string HeaderEchoPath = "/echo-proxy-secret-header";

    /// <summary>Response header <see cref="HeaderEchoPath"/> answers with.</summary>
    public const string SawProxySecretHeader = "X-Test-Saw-Proxy-Secret";

    /// <summary>Long enough that no bucket refills during a test that is filling one.</summary>
    public static readonly TimeSpan StableRefill = TimeSpan.FromMinutes(10);

    public static int AuthenticatedBurst => GatewayRateLimitOptions.DefaultAuthenticatedBurstCapacity;

    public static int AnonymousBurst => GatewayRateLimitOptions.DefaultAnonymousBurstCapacity;

    private readonly WebApplication _app;

    public RecordingLoggerProvider Logs { get; }

    private GatewayRateLimitHarness(WebApplication app, RecordingLoggerProvider logs)
    {
        _app = app;
        Logs = logs;
    }

    /// <summary>
    /// Production token counts, a refill slow enough to be invisible, and whichever trust
    /// configuration the test is about.
    /// </summary>
    public static GatewayRateLimitOptions Options(
        TrustedProxyOptions? trustedProxies = null,
        ProxySecretOptions? proxySecret = null,
        TimeSpan? replenishmentPeriod = null) => new()
    {
        TrustedProxies = trustedProxies ?? new TrustedProxyOptions(),
        ProxySecret = proxySecret ?? new ProxySecretOptions(),
        ReplenishmentPeriod = replenishmentPeriod ?? StableRefill
    };

    public static async Task<GatewayRateLimitHarness> StartAsync(GatewayRateLimitOptions? options = null)
    {
        // The provider is created before the host so that startup-time lines - the trust-
        // configuration warning UseEchoRateLimiter emits while the pipeline is being built - are
        // captured too.
        var logs = new RecordingLoggerProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var harness = new GatewayRateLimitHarness(BuildApp(builder, options ?? Options(), logs), logs);

        await harness._app.StartAsync();
        return harness;
    }

    private static WebApplication BuildApp(WebApplicationBuilder builder, GatewayRateLimitOptions options, RecordingLoggerProvider logs)
    {
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        builder.Services.AddEchoRateLimiting(options);

        var app = builder.Build();

        // Stands in for app.UseAuthentication(): populates HttpContext.User before the limiter
        // runs, which is the ordering constraint the partitioner depends on.
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers[SubjectHeader].ToString();
            if (!string.IsNullOrEmpty(subject))
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "TestAuth"));
            }

            await next(context);
        });

        app.UseEchoRateLimiter();

        app.MapGet(ProxiedPath, () => "ok").RequireRateLimiting(GatewayRateLimiting.PolicyName);
        app.MapGet(OtherProxiedPath, () => "ok").RequireRateLimiting(GatewayRateLimiting.PolicyName);
        app.MapGet("/api/webhooks/{webhookId}/{token}", () => "ok").RequireRateLimiting(GatewayRateLimiting.PolicyName);
        app.MapGet(UnlimitedPath, () => "ok");
        app.MapGet(HeaderEchoPath, (HttpContext context) =>
        {
            // Reported as a response header rather than a body: TestServer's response body is a
            // forward-only stream, and what is being asserted is presence, not content.
            context.Response.Headers[SawProxySecretHeader] =
                context.Request.Headers.ContainsKey(ProxySecretOptions.HeaderName) ? "yes" : "no";
            return "ok";
        });

        return app;
    }

    /// <summary>Sends one request through the whole pipeline.</summary>
    public async Task<HttpContext> SendAsync(
        string path = ProxiedPath,
        string? subject = null,
        string peer = "203.0.113.5",
        string? forwardedFor = null,
        string? proxySecret = null,
        string[]? proxySecretValues = null)
    {
        return await _app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            if (subject is not null) context.Request.Headers[SubjectHeader] = subject;
            if (forwardedFor is not null) context.Request.Headers[ClientIpResolver.ForwardedForHeader] = forwardedFor;
            if (proxySecret is not null) context.Request.Headers[ProxySecretOptions.HeaderName] = proxySecret;
            if (proxySecretValues is not null) context.Request.Headers[ProxySecretOptions.HeaderName] = proxySecretValues;
        });
    }

    /// <summary>Sends <paramref name="count"/> requests and returns the status codes in order.</summary>
    public async Task<List<int>> SendManyAsync(
        int count,
        string path = ProxiedPath,
        string? subject = null,
        string peer = "203.0.113.5",
        string? forwardedFor = null,
        string? proxySecret = null)
    {
        var statuses = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var context = await SendAsync(path, subject, peer, forwardedFor, proxySecret);
            statuses.Add(context.Response.StatusCode);
        }

        return statuses;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
