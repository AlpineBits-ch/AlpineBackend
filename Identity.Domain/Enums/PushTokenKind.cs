namespace Identity.Domain.Enums;

/// <summary>Which push transport a <see cref="Entities.UserPushToken"/> addresses.</summary>
public enum PushTokenKind
{
    /// <summary>Firebase Cloud Messaging - regular notifications on Android, and the data-only
    /// message the Android call flow rings on.</summary>
    Fcm,

    /// <summary>APNs PushKit VoIP token. iOS only, and the one path FCM cannot cover because
    /// CallKit requires a VoIP push.</summary>
    ApnsVoip,

    /// <summary>
    /// A W3C Push API subscription from a browser (RFC 8030), for the web client.
    /// </summary>
    WebPush,
}

/// <summary>Questions senders ask about a transport, in one place rather than as a <c>switch</c> in
/// each of the nine handlers that send push.</summary>
public static class PushTokenKindExtensions
{
    /// <summary>
    /// Whether a payload that produces no visible notification will still be delivered.
    /// </summary>
    public static bool CanReceiveSilentPush(this PushTokenKind kind) => kind is not PushTokenKind.WebPush;
}
