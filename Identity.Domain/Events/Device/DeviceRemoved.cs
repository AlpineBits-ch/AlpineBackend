namespace Identity.Domain.Events.Device;

public class DeviceRemoved
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
}