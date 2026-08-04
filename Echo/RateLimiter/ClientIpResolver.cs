using System.Net;

namespace Echo.RateLimiter;

/// <summary>
/// Works out which address a rate-limit partition should be charged to.
///
/// <para>Deliberately scoped to the rate limiter rather than implemented as a global
/// <c>UseForwardedHeaders</c>: rewriting <see cref="ConnectionInfo.RemoteIpAddress"/> for the whole
/// gateway would change the <c>X-Forwarded-For</c> chain that YARP appends on the way out to every
/// downstream service, and those services are entitled to keep the semantics they were written
/// against. The limiter is the only component here that needs a client identity, so it is the only
/// one that gets one.</para>
/// </summary>
public static class ClientIpResolver
{
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>
    /// Returns the address to partition on, or null when there is no connection information at all
    /// (in-memory hosts). Callers should treat null as "unknown" rather than as a valid key.
    /// </summary>
    public static IPAddress? Resolve(HttpContext context, GatewayRateLimitOptions options)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null) return null;

        peer = TrustedProxyOptions.Normalize(peer);

        // Nothing vouches for this request's forwarded chain: the socket address is the only thing
        // we have any reason to believe.
        if (!IsForwardedChainTrusted(context, options, peer)) return peer;

        var header = context.Request.Headers[ForwardedForHeader];
        if (header.Count == 0) return peer;

        // X-Forwarded-For is append-only and left-to-right oldest-first, so the trustworthy part of
        // the chain is the right-hand end - everything a proxy we trust actually wrote. Walk right
        // to left, skipping hops that are themselves trusted proxies, and stop at the first address
        // that isn't: that is the closest hop we did not vouch for, i.e. the real client. Anything
        // further left was supplied by the client and is free-form attacker input.
        var entries = new List<string>();
        foreach (var value in header)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            entries.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!TryParseAddress(entries[i], out var candidate)) break;
            if (options.TrustedProxies.IsTrustedProxy(candidate)) continue;
            return candidate;
        }

        return peer;
    }

    /// <summary>
    /// Whether this request's <c>X-Forwarded-For</c> chain may be believed. Two independent
    /// mechanisms, either of which is sufficient:
    ///
    /// <list type="bullet">
    /// <item>the immediate peer is inside <see cref="TrustedProxyOptions"/> - correct only where
    /// proxy addresses are stable;</item>
    /// <item>the request carried the <see cref="ProxySecretOptions.HeaderName"/> header with the
    /// configured secret - the recommended mechanism, and the only one that survives a container or
    /// load balancer changing address.</item>
    /// </list>
    ///
    /// <para>The secret is normally evaluated once, in
    /// <see cref="GatewayRateLimiting.UseEchoRateLimiter"/>, which then removes the header from the
    /// request so it can never be proxied onward; the outcome is left in
    /// <see cref="HttpContext.Items"/> under <see cref="ProxySecretOptions.ContextItemKey"/>. The
    /// direct header read below is the fallback for a caller that reaches this method outside that
    /// pipeline.</para>
    /// </summary>
    public static bool IsForwardedChainTrusted(HttpContext context, GatewayRateLimitOptions options, IPAddress peer)
    {
        if (options.TrustedProxies.IsTrustedProxy(peer)) return true;

        if (context.Items.TryGetValue(ProxySecretOptions.ContextItemKey, out var evaluated) && evaluated is bool matched)
        {
            return matched;
        }

        return options.ProxySecret.Matches(context.Request.Headers[ProxySecretOptions.HeaderName]);
    }

    /// <summary>
    /// Accepts both the bare-address form nginx/Caddy emit and the <c>host:port</c> /
    /// <c>[v6]:port</c> form some proxies use.
    /// </summary>
    private static bool TryParseAddress(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed))
        {
            address = TrustedProxyOptions.Normalize(parsed);
            return true;
        }

        if (IPEndPoint.TryParse(value, out var endpoint))
        {
            address = TrustedProxyOptions.Normalize(endpoint.Address);
            return true;
        }

        address = IPAddress.None;
        return false;
    }
}
