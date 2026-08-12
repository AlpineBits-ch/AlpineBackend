using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

/// <summary>Body of the deprecated per-transport token endpoints.</summary>
public class CreateDeviceTokenDto
{

    public string Token { get; set; }

    /// <summary>Optional even on the legacy endpoints - an installation that sends it gets its
    /// token attached to the device, which is what makes targeted sends and cleanup possible.</summary>
    public string? DeviceId { get; set; }
}

public class CreatePushTokenDto
{
    /// <summary>The transport token.</summary>
    public string? Token { get; set; }

    /// <summary><c>Fcm</c> for Firebase (Android notifications and the Android call ring),
    /// <c>ApnsVoip</c> for the iOS PushKit token CallKit needs, <c>WebPush</c> for a browser
    /// subscription.</summary>
    public PushTokenKind Kind { get; set; }

    /// <summary>The client device id (same value as the <c>X-Device-Id</c> header and the MLS
    /// ClientDeviceId). Optional, but without it the token can't be targeted or cleaned up.</summary>
    public string? DeviceId { get; set; }

    /// <summary><c>PushSubscription.endpoint</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary><c>PushSubscription.keys.p256dh</c> - base64url uncompressed P-256 point, 87 chars.
    /// Required when <see cref="Kind"/> is <c>WebPush</c>.</summary>
    public string? P256dh { get; set; }

    /// <summary><c>PushSubscription.keys.auth</c> - 16 random bytes as 22 chars of base64url.
    /// Required when <see cref="Kind"/> is <c>WebPush</c>.</summary>
    public string? Auth { get; set; }
}
