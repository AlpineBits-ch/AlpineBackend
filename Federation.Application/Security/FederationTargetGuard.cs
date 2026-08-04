using System.Net;
using System.Net.Sockets;
using AppEnvironment.Security;

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
        bool allowPrivateTargets) => OutboundAddressGuard.CreateConnectCallback(allowPrivateTargets);

    private static bool IsBlockedAddress(IPAddress address) => OutboundAddressGuard.IsBlocked(address);
}
