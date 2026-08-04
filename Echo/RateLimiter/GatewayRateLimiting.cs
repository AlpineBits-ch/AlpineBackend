using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Echo.RateLimiter;

/// <summary>The gateway's request rate limiter.</summary>
public static class GatewayRateLimiting
{
    /// <summary>
    /// The name <see cref="RateLimitConfigFilter"/> stamps onto every proxied route.
    /// </summary>
    public const string PolicyName = "PerUserPolicy";

    public const string LoggerCategory = "Echo.RateLimiter";

    /// <summary>
    /// Where <see cref="GetPartition"/> records the size of the bucket this request was charged to,
    /// so the rejection handler can report a truthful <c>X-RateLimit-Limit</c> without having to
    /// re-derive which partition was chosen.
    /// </summary>
    private const string BucketCapacityItemKey = "Echo.RateLimiter.BucketCapacity";

    public static IServiceCollection AddEchoRateLimiting(this IServiceCollection services, GatewayRateLimitOptions? options = null)
    {
        var resolved = options ?? GatewayRateLimitOptions.FromEnvironment();
        services.AddSingleton(resolved);

        // Strips ProxySecretOptions.HeaderName from every proxied request on the way out.
        services.AddSingleton<ITransformProvider, ProxySecretStrippingTransformProvider>();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, context => GetPartition(context, resolved));
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;
        });

        return services;
    }

    /// <summary>Installs the limiter middleware.</summary>
    public static WebApplication UseEchoRateLimiter(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<GatewayRateLimitOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        LogConfiguration(logger, options);

        // Evaluate the proxy secret once, then take the header off the request.
        app.Use(async (context, next) =>
        {
            context.Items[ProxySecretOptions.ContextItemKey] =
                options.ProxySecret.Matches(context.Request.Headers[ProxySecretOptions.HeaderName]);
            context.Request.Headers.Remove(ProxySecretOptions.HeaderName);

            await next(context);
        });

        app.UseRateLimiter();
        return app;
    }

    private static void LogConfiguration(ILogger logger, GatewayRateLimitOptions options)
    {
        // "Set, but to nothing" is a different mistake from "never set", and the likeliest way to
        // make it is a stray newline or a quoted empty string in a hand-edited .env.
        if (options.ProxySecret.WasSetButBlank)
        {
            logger.LogWarning(
                "{Variable} is set but contains only whitespace. It is being treated as unset: no forwarded header will be believed on the strength of the {Header} header.",
                ProxySecretOptions.EnvironmentVariable, ProxySecretOptions.HeaderName);
        }

        var budgets =
            $"authenticated {options.AuthenticatedTokensPerPeriod}/burst {options.AuthenticatedBurstCapacity}, " +
            $"anonymous {options.AnonymousTokensPerPeriod}/burst {options.AnonymousBurstCapacity}, " +
            $"webhook {options.WebhookTokensPerPeriod}/burst {options.WebhookBurstCapacity}, " +
            $"replenished every {options.ReplenishmentPeriod}";

        if (options.ProxySecret.IsConfigured)
        {
            logger.LogInformation(
                "Gateway rate limiting enabled ({Budgets}). Client addresses are taken from {ForwardedHeader} when the {Header} header matches {Variable}; the header is stripped before proxying downstream.",
                budgets, ClientIpResolver.ForwardedForHeader, ProxySecretOptions.HeaderName, ProxySecretOptions.EnvironmentVariable);
        }
        else if (options.TrustedProxies.HasTrustedProxies)
        {
            logger.LogInformation(
                "Gateway rate limiting enabled ({Budgets}). Client addresses are taken from {ForwardedHeader} for {ProxyCount} trusted proxy entries. " +
                "Consider {Variable} instead: an address allowlist stops matching, silently, whenever a container or load balancer changes address.",
                budgets, ClientIpResolver.ForwardedForHeader,
                options.TrustedProxies.KnownProxies.Count + options.TrustedProxies.KnownNetworks.Count,
                ProxySecretOptions.EnvironmentVariable);
        }
        else
        {
            // Not merely noisy: if this gateway is behind nginx/Caddy (which every shipped deploy
            // profile is), the peer address is the proxy and every anonymous caller in the world
            // now shares one bucket - login and registration included.
            logger.LogWarning(
                "Gateway rate limiting enabled ({Budgets}) but neither {Variable} nor {LegacyVariable} is configured. " +
                "Anonymous callers are partitioned on the immediate peer address; behind a reverse proxy that is a single shared bucket for all of them. " +
                "Set the former on the gateway and have the reverse proxy send the same value as the {Header} header.",
                budgets, ProxySecretOptions.EnvironmentVariable, TrustedProxyOptions.EnvironmentVariable, ProxySecretOptions.HeaderName);
        }
    }

    /// <summary>Chooses the bucket a request is charged to.</summary>
    public static RateLimitPartition<string> GetPartition(HttpContext context, GatewayRateLimitOptions options)
    {
        // Webhook execution is authenticated by a token in the path, not by a signed-in user, so
        // the usual identity partition doesn't exist for it.
        if (TryGetWebhookId(context.Request.Path, out var webhookId))
        {
            return TokenBucket(context, $"webhook:{webhookId}", options.WebhookBurstCapacity, options.WebhookTokensPerPeriod, options);
        }

        var subject = ResolveSubject(context.User);
        if (subject is not null)
        {
            return TokenBucket(context, $"user:{subject}", options.AuthenticatedBurstCapacity, options.AuthenticatedTokensPerPeriod, options);
        }

        var clientIp = ClientIpResolver.Resolve(context, options);
        return TokenBucket(context, clientIp is null ? "ip:unknown" : $"ip:{clientIp}",
            options.AnonymousBurstCapacity, options.AnonymousTokensPerPeriod, options);
    }

    /// <summary>The caller's stable identity.</summary>
    public static string? ResolveSubject(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? user.FindFirstValue("sub")
                      ?? user.Identity.Name;

        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    /// <summary>
    /// A token bucket: <paramref name="capacity"/> requests may be spent at once, refilling at
    /// <paramref name="tokensPerPeriod"/> per <see
    /// cref="GatewayRateLimitOptions.ReplenishmentPeriod"/>.
    /// </summary>
    private static RateLimitPartition<string> TokenBucket(
        HttpContext context, string key, int capacity, int tokensPerPeriod, GatewayRateLimitOptions options)
    {
        context.Items[BucketCapacityItemKey] = capacity;

        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = capacity,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = options.ReplenishmentPeriod,
            AutoReplenishment = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    }

    /// <summary>
    /// A 429 that tells the client when to come back, and tells the operator that it happened.
    /// </summary>
    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetService<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();

        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
            ? metadata
            : options.ReplenishmentPeriod;

        // Two resolutions of the same number on purpose.
        var retryAfterSeconds = Math.Round(Math.Max(0, retryAfter.TotalSeconds), 3);
        var retryAfterWholeSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        var capacity = httpContext.Items.TryGetValue(BucketCapacityItemKey, out var stored) && stored is int value
            ? value
            : options.AuthenticatedBurstCapacity;

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = retryAfterWholeSeconds.ToString(CultureInfo.InvariantCulture);
        // The gateway deliberately mirrors Discord's surface elsewhere (see ProxyConfig's
        // discord-compat and webhook routes), so an off-the-shelf Discord client library already
        // knows how to back off when it sees these.
        httpContext.Response.Headers["X-RateLimit-Limit"] = capacity.ToString(CultureInfo.InvariantCulture);
        httpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
        httpContext.Response.Headers["X-RateLimit-Reset-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        logger.LogWarning(
            "Rate limit exceeded: {Method} {Path} subject={Subject} clientIp={ClientIp} retryAfterSeconds={RetryAfter}",
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            ResolveSubject(httpContext.User) ?? "(anonymous)",
            ClientIpResolver.Resolve(httpContext, options)?.ToString() ?? "(unknown)",
            retryAfterSeconds);

        if (httpContext.Response.HasStarted) return;

        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response.WriteAsync(JsonSerializer.Serialize(new RateLimitRejection
        {
            Message = "You are being rate limited.",
            RetryAfter = retryAfterSeconds,
            // This is the global limit: one bucket per subject covering every proxied route, which
            // is exactly what Discord's `global` flag means.
            Global = true
        }), cancellationToken);
    }

    /// <summary>
    /// Extracts the webhook id from "/api/webhooks/{webhookId}/{token}", the one unauthenticated
    /// write route on the gateway (see the webhook-execute-route in ProxyConfig).
    /// </summary>
    public static bool TryGetWebhookId(PathString path, out string webhookId)
    {
        webhookId = string.Empty;
        if (!path.HasValue) return false;

        var value = path.Value!;
        const string prefix = "/api/webhooks/";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = value[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;

        webhookId = rest[..slash];
        return true;
    }
}

/// <summary>
/// Body of a 429. Field names match Discord's rate-limit response so existing bot libraries
/// pointed at the discord-compat surface parse it without changes - including
/// <c>retry_after</c> being fractional seconds rather than whole ones.
/// </summary>
public sealed class RateLimitRejection
{
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("retry_after")]
    public double RetryAfter { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("global")]
    public bool Global { get; init; }
}
