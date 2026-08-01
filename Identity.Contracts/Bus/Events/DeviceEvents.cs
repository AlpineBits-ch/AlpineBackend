namespace Identity.Contracts.Bus.Events;

/// <summary>Published when a user registers a new device.
///
/// <para>Lives in Contracts rather than Identity.Domain because the consumers that matter are in
/// other services: an MLS group member is a <i>device</i>, so every encrypted context the user is
/// in has to learn that a new leaf wants in. A domain-internal event could not be subscribed to
/// from Messaging at all, which is precisely how this event ended up with no consumer anywhere.</para></summary>
public class DeviceRegistered
{
    public string UserId { get; init; } = null!;

    /// <summary>Row id, the value Identity's own tables key off.</summary>
    public string DeviceId { get; init; } = null!;

    /// <summary>The id every other service and every client knows the device by - the value sent as
    /// <c>X-Device-Id</c> and the one a Welcome is addressed to.</summary>
    public string ClientDeviceId { get; init; } = null!;

    public string DeviceName { get; init; } = null!;

    /// <summary>True when this registration replaced the device's identity key rather than creating
    /// a new device. The old key packages are gone, and every group the device is in now holds a
    /// leaf it can no longer sign for - it has to be re-admitted, not merely topped up.</summary>
    public bool IdentityRotated { get; init; }
}

/// <summary>Published when a user unregisters one of their devices. Carries both ids because
/// <see cref="DeviceId"/> (the row id) is what Identity's own tables key off, while
/// <see cref="ClientDeviceId"/> is the id every other service and every client knows the device
/// by - it is the value sent as <c>X-Device-Id</c>.</summary>
public class DeviceRemoved
{
    public string UserId { get; init; } = null!;
    public string DeviceId { get; init; } = null!;
    public string ClientDeviceId { get; init; } = null!;
}
