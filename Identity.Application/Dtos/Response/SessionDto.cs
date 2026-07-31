using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

public class SessionDto
{
    public string Id { get; set; } = null!;
    public string DeviceName { get; set; } = null!;
    public DeviceType DeviceType { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public bool IsCurrent { get; set; }

    /// <summary>The registered device this login came from, by its client-supplied id. Null for
    /// logins that didn't send one (older builds, and every session created before sessions were
    /// linked to devices).</summary>
    public string? ClientDeviceId { get; set; }
}
