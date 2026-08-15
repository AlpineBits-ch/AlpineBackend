using System.Net;
using System.Net.Sockets;

namespace AppEnvironment.Security;

/// <summary>Refuses to open sockets to addresses that are not on the public internet.</summary>
public static class OutboundAddressGuard
{
    /// <summary>A connect callback that dials only routable addresses.</summary>
    /// <param name="allowPrivateTargets">
    /// True outside Production, so a self-hoster, a developer, or the E2E harness can point these
    /// clients at localhost.
    /// </param>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
        bool allowPrivateTargets)
    {
        return async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

            var permitted = allowPrivateTargets
                ? addresses
                : addresses.Where(a => !IsBlocked(a)).ToArray();

            if (permitted.Length == 0)
                throw new HttpRequestException(
                    $"Refusing to connect to '{context.DnsEndPoint.Host}': resolves only to non-routable addresses.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    /// <summary>
    /// Whether this address is off-limits: loopback, link-local (including the cloud metadata
    /// endpoint), RFC1918, CGNAT, IPv6 unique-local, multicast and the reserved ranges.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return true;

            // Unique local (fc00::/7).
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return true;

            // IPv4-mapped (::ffff:a.b.c.d) - unwrap so the IPv4 rules below still apply.
            if (address.IsIPv4MappedToIPv6) return IsBlocked(address.MapToIPv4());

            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return true;

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                  // 0.0.0.0/8
            10 => true,                                 // 10.0.0.0/8
            127 => true,                                // loopback
            169 when b[1] == 254 => true,               // 169.254.0.0/16 incl. cloud metadata
            172 when b[1] >= 16 && b[1] <= 31 => true,  // 172.16.0.0/12
            192 when b[1] == 168 => true,               // 192.168.0.0/16
            100 when b[1] >= 64 && b[1] <= 127 => true, // 100.64.0.0/10 CGNAT
            >= 224 => true,                             // multicast + reserved
            _ => false
        };
    }

    /// <summary>
    /// Validates a caller-supplied URL before it is ever dialled: absolute, http/https only, no
    /// embedded credentials, and not a literal non-routable address.
    /// </summary>
    public static bool TryValidate(string? url, bool allowPrivateTargets, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "URL is required.";
            return false;
        }

        // A substring test for "/invite/" used to sit here, as a stand-in for "do not unfurl our
        // own invite links".

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "URL must be absolute.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = "URL must use http or https.";
            return false;
        }

        // user:password@host would be sent to the origin, so a crafted link could make the server
        // hand a token to a host of the attacker's choosing.
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URL must not embed credentials.";
            return false;
        }

        if (IPAddress.TryParse(parsed.DnsSafeHost, out var literal)
            && !allowPrivateTargets
            && IsBlocked(literal))
        {
            error = "URL resolves to a non-routable address.";
            return false;
        }

        uri = parsed;
        return true;
    }
}
