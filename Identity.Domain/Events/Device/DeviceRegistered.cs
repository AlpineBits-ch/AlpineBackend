namespace Identity.Domain.Events.Device;

public class DeviceRegistered
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
    public string DeviceName { get; init; }
}