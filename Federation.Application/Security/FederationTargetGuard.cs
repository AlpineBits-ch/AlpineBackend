using System.Net;
using System.Net.Sockets;
using AppEnvironment.Security;

namespace Federation.Application.Security;

public static class FederationHttpClients
{
    /// <summary>Named client for outbound calls whose target host is caller-supplied. Redirects
    /// are disabled and every connection attempt is IP-checked - see
    /// <see cref="FederationTargetGuard"/>.</summary>
    public const string Handshake = "federation-handshake";
}

/// <summary>
/// Guards outbound federation requests against SSRF. The handshake target is supplied in the
/// request body, so without this an operator-reachable endpoint could be pointed at cloud metadata
/// (169.254.169.254), in-cluster services, or localhost.
///
/// Validation happens at <c>ConnectCallback</c> time against the IP actually being dialled rather
/// than only up-front against the hostname, which is what makes DNS rebinding (a name that
/// resolves to a public address on the pre-flight check and a private one on the real connect)
/// ineffective. Redirect following is disabled separately, so a public host cannot bounce the
/// request inward either.
///
/// <para>The address rules and the connect callback now live in
/// <see cref="OutboundAddressGuard"/>, shared with the link unfurler - which takes URLs from anyone
/// who can type in a chat box and therefore needs exactly the same defence. What stays here is the
/// federation-specific policy on top: https is mandatory in Production, and the error strings are
/// the ones the admin API returns.</para>
/// </summary>
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

        // Reject a literal private address early for a clear error message. A hostname that
        // resolves to one is caught at connect time instead.
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
