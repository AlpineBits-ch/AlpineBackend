namespace Identity.Domain.Events.Device;

/// <summary>Published when a user unregisters one of their devices. Carries both ids because
/// <see cref="DeviceId"/> (the row id) is what Identity's own tables key off, while
/// <see cref="ClientDeviceId"/> is the id every other service and every client knows the device
/// by - it is the value sent as <c>X-Device-Id</c>.</summary>
public class DeviceRemoved
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
    public string ClientDeviceId { get; init; }
}
