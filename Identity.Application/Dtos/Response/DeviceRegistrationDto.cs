using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>What <c>POST api/v1/devices</c> returns.</summary>
public class DeviceRegistrationDto
{
    public string Id { get; set; } = null!;
    public string ClientDeviceId { get; set; } = null!;
    public string DeviceName { get; set; } = null!;
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; } = null!;
    public DeviceStatus Status { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string UserId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this call replaced the device's identity public key.</summary>
    public bool IdentityRotated { get; set; }
}

/// <summary>Result of <c>DELETE api/v1/devices/client/{deviceId}/key-packages</c>.</summary>
public class ResetKeyPackagesResultDto
{
    public int DeletedCount { get; set; }
}
