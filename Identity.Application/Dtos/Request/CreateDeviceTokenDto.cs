using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

/// <summary>Body of the deprecated per-transport token endpoints. Kept so builds already in the
/// wild keep registering; <see cref="CreatePushTokenDto"/> is the shape to move to.</summary>
public class CreateDeviceTokenDto
{

    public string Token { get; set; }

    /// <summary>Optional even on the legacy endpoints - an installation that sends it gets its
    /// token attached to the device, which is what makes targeted sends and cleanup possible.</summary>
    public string? DeviceId { get; set; }
}

public class CreatePushTokenDto
{
    public string Token { get; set; } = null!;

    /// <summary><c>Fcm</c> for Firebase (Android notifications and the Android call ring),
    /// <c>ApnsVoip</c> for the iOS PushKit token CallKit needs.</summary>
    public PushTokenKind Kind { get; set; }

    /// <summary>The client device id (same value as the <c>X-Device-Id</c> header and the MLS
    /// ClientDeviceId). Optional, but without it the token can't be targeted or cleaned up.</summary>
    public string? DeviceId { get; set; }
}
