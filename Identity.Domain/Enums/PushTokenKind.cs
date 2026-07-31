namespace Identity.Domain.Enums;

/// <summary>
/// Which push transport a <see cref="Entities.UserPushToken"/> addresses. Both kinds used to live
/// in their own table (UserDeviceToken / UserVoipToken) with identical columns; the transport is
/// the only thing that ever differed, so it is a column now.
/// </summary>
public enum PushTokenKind
{
    /// <summary>Firebase Cloud Messaging - regular notifications on Android, and the data-only
    /// message the Android call flow rings on.</summary>
    Fcm,

    /// <summary>APNs PushKit VoIP token. iOS only, and the one path FCM cannot cover because
    /// CallKit requires a VoIP push.</summary>
    ApnsVoip,
}
