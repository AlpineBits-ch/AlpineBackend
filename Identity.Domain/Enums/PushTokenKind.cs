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
}
