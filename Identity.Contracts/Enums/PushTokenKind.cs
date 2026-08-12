namespace Identity.Contracts.Enums;

/// <summary>Wire copy of Identity.Domain.Enums.PushTokenKind.</summary>
public enum PushTokenKind
{
    Fcm,
    ApnsVoip,

    /// <summary>A browser Push API subscription (RFC 8030).</summary>
    WebPush,
}

/// <summary>Wire copy of <c>Identity.Domain.Enums.PushTokenKindExtensions</c>.</summary>
public static class PushTokenKindExtensions
{
    /// <summary>False for <see cref="PushTokenKind.WebPush"/>, because Chrome enforces
    /// <c>userVisibleOnly: true</c> on every subscription. A data-only send must filter on this rather
    /// than deliver a push the browser answers with its own "site updated in the background"
    /// toast.</summary>
    public static bool CanReceiveSilentPush(this PushTokenKind kind) => kind is not PushTokenKind.WebPush;
}
