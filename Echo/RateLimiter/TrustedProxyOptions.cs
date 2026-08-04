using System.Net;

namespace Echo.RateLimiter;

/// <summary>
/// The set of network peers the gateway is willing to believe when they claim, via
/// <c>X-Forwarded-For</c>, to be speaking on behalf of somebody else.
///
/// <para>This exists because the gateway's anonymous rate-limit partition is the caller's IP
/// address, and in every real deployment the gateway is not the edge: <c>nginx.conf</c> and the
/// bundled Caddy both terminate TLS and proxy to Echo over loopback or a container network. To
/// Echo, <see cref="ConnectionInfo.RemoteIpAddress"/> is therefore the *proxy*, identical for
/// every caller on the planet - which would collapse all anonymous traffic (login, registration,
/// password reset) into a single 100/min bucket and take the instance down for everyone the
/// moment one client got busy.</para>
///
/// <para>The naive fix - trust <c>X-Forwarded-For</c> unconditionally - is worse: an attacker
/// then mints a fresh partition key per request by varying a header, and the limiter enforces
/// nothing at all. So a forwarded address is only believed when something vouches for it. Left
/// unconfigured, the gateway ignores the header entirely and stays on the (safe but coarse) peer
/// address; <see cref="GatewayRateLimiting"/> logs a startup warning in that case so the collapse
/// is never silent.</para>
///
/// <para><b>This is the alternative mechanism, not the recommended one.</b> An address allowlist is
/// only correct where the proxy's address is stable and stays that way; where it is not, the list
/// drifts and stops matching, and the failure is silent - forwarded headers quietly stop being
/// believed and everything collapses into the shared bucket again. Prefer
/// <see cref="ProxySecretOptions"/>, which binds trust to a configured secret rather than to the
/// network topology. Both are honoured, and either one matching is enough (see
/// <see cref="ClientIpResolver.IsForwardedChainTrusted"/>), so a deployment that does have stable
/// ranges can keep using this one.</para>
/// </summary>
public sealed class TrustedProxyOptions
{
    /// <summary>
    /// Comma/semicolon/whitespace separated list of proxy addresses or CIDR ranges, e.g.
    /// <c>GATEWAY_TRUSTED_PROXIES="127.0.0.1,::1,172.16.0.0/12"</c>.
    /// </summary>
    public const string EnvironmentVariable = "GATEWAY_TRUSTED_PROXIES";

    public List<IPAddress> KnownProxies { get; } = [];

    public List<System.Net.IPNetwork> KnownNetworks { get; } = [];

    /// <summary>
    /// False when nothing is configured, in which case forwarded headers are ignored outright.
    /// </summary>
    public bool HasTrustedProxies => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    /// <summary>
    /// Reads the trust list from the environment. Unparseable entries are skipped rather than
    /// throwing: a typo in one CIDR must not take the whole gateway down at boot, and the
    /// resulting behaviour (that peer is untrusted) is the safe direction to fail.
    /// </summary>
    public static TrustedProxyOptions FromEnvironment(string? raw = null)
    {
        raw ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        var options = new TrustedProxyOptions();
        if (string.IsNullOrWhiteSpace(raw)) return options;

        foreach (var entry in raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (System.Net.IPNetwork.TryParse(entry, out var network)) options.KnownNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(Normalize(address));
            }
        }

        return options;
    }

    public bool IsTrustedProxy(IPAddress? address)
    {
        if (address is null) return false;
        var normalized = Normalize(address);

        foreach (var proxy in KnownProxies)
        {
            if (proxy.Equals(normalized)) return true;
        }

        foreach (var network in KnownNetworks)
        {
            // IPNetwork.Contains throws nothing but returns false across address families, so the
            // v4/v6-mapped normalisation above is what makes "127.0.0.1/8" match a ::ffff:127.0.0.1
            // peer, which is exactly how Kestrel reports a loopback connection on a dual-stack socket.
            if (network.Contains(normalized)) return true;
        }

        return false;
    }

    /// <summary>
    /// Collapses IPv4-mapped IPv6 addresses to plain IPv4 and drops the IPv6 scope id, so that the
    /// same physical client always produces the same partition key regardless of which socket
    /// flavour Kestrel happened to accept it on.
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            address = new IPAddress(address.GetAddressBytes());
        }
        return address;
    }
}
