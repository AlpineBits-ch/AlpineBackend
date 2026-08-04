using System.Net;

namespace Echo.RateLimiter;

/// <summary>
/// The set of network peers the gateway is willing to believe when they claim, via
/// <c>X-Forwarded-For</c>, to be speaking on behalf of somebody else.
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

    /// <summary>Reads the trust list from the environment.</summary>
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
