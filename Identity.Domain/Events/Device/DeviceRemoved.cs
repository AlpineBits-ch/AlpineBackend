namespace Identity.Domain.Events.Device;

/// <summary>Published when a user unregisters one of their devices.</summary>
public class DeviceRemoved
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
    public string ClientDeviceId { get; init; }
}
