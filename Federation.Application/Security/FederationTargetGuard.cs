using System.Net;
using System.Net.Sockets;

namespace Federation.Application.Security;

public static class FederationHttpClients
{
    /// <summary>Named client for outbound calls whose target host is caller-supplied.</summary>
    public const string Handshake = "federation-handshake";
}

/// <summary>Guards outbound federation requests against SSRF.</summary>
public static class FederationTargetGuard
{
    /// <summary>Validates the user-supplied target and returns the normalized absolute URI.</summary>
    /// <param name="allowPrivateTargets">
    /// True outside Production, so a self-hoster or the E2E harness can federate two instances on
    /// localhost. Never true in Production.
    /// </param>
    public static bool TryNormalizeTarget(string? targetHost, bool allowPrivateTargets, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(targetHost))
        {
            error = "Target host is required.";
            return false;
        }

        if (!Uri.TryCreate(targetHost.TrimEnd('/'), UriKind.Absolute, out var parsed))
        {
            error = "Target host must be an absolute URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = "Target host must use http or https.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !allowPrivateTargets)
        {
            error = "Federation targets must use https.";
            return false;
        }

        // Reject a literal private address early for a clear error message.
        if (IPAddress.TryParse(parsed.DnsSafeHost, out var literal)
            && !allowPrivateTargets
            && IsBlockedAddress(literal))
        {
            error = "Target host resolves to a non-routable address.";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that refuses to open a socket to a
    /// non-routable address.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
        bool allowPrivateTargets)
    {
        return async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

            var permitted = allowPrivateTargets
                ? addresses
                : addresses.Where(a => !IsBlockedAddress(a)).ToArray();

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

    private static bool IsBlockedAddress(IPAddress address)
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
            if (address.IsIPv4MappedToIPv6) return IsBlockedAddress(address.MapToIPv4());

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
}
