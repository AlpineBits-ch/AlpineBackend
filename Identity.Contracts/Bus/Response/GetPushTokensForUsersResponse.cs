using Identity.Contracts.Enums;

namespace Identity.Contracts.Bus.Response;

public class PushTokenResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>The value a send addresses: an FCM/APNs token, or - when <see cref="Kind"/> is
    /// <c>WebPush</c> - the RFC 8030 subscription endpoint URL. See <c>UserPushToken.Token</c> for why
    /// the endpoint is carried here rather than in a field of its own.</summary>
    public string Token { get; set; } = null!;

    public PushTokenKind Kind { get; set; }

    /// <summary>The subscription's ECDH public key (<c>p256dh</c>), base64url.</summary>
    public string? P256dh { get; set; }

    /// <summary>The subscription's auth secret, base64url. Set only for <c>WebPush</c>.</summary>
    public string? Auth { get; set; }

    /// <summary>
    /// Whether a payload that draws no visible notification will still be delivered here.
    /// </summary>
    public bool CanReceiveSilentPush => Kind.CanReceiveSilentPush();

    /// <summary>Whether this row carries everything its transport needs.</summary>
    public bool IsSendable => Kind != PushTokenKind.WebPush
                              || (!string.IsNullOrWhiteSpace(P256dh) && !string.IsNullOrWhiteSpace(Auth));

    /// <summary>The client device id this token was registered from, or null for tokens
    /// registered without one.</summary>
    public string? ClientDeviceId { get; set; }

    /// <summary>What the build behind this token declared it understands (see
    /// <c>Domain.PushCapabilities</c>). Empty both for a token with no device attached and for a
    /// device that never declared anything - which are the same answer for a sender's purposes:
    /// send the shape every client has always understood.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Reads as "this handset can definitely handle that", which is the only question a
    /// sender asks. Ordinal because these are protocol tokens, not words.</summary>
    public bool Supports(string capability) =>
        Capabilities.Contains(capability, StringComparer.Ordinal);
}

public class GetPushTokensForUsersResponse
{
    public ICollection<PushTokenResponse> Tokens { get; set; } = new List<PushTokenResponse>();

    public IEnumerable<string> Of(PushTokenKind kind) =>
        Tokens.Where(t => t.Kind == kind).Select(t => t.Token);

    /// <summary>The endpoints a data-only push can actually be delivered to.</summary>
    public IEnumerable<PushTokenResponse> SilentCapable =>
        Tokens.Where(t => t.CanReceiveSilentPush && t.IsSendable);

    /// <summary>Rows of one kind that carry everything needed to send to them.</summary>
    public IEnumerable<PushTokenResponse> Sendable(PushTokenKind kind) =>
        Tokens.Where(t => t.Kind == kind && t.IsSendable);
}
