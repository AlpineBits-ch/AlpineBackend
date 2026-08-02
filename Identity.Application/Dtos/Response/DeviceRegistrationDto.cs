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

/// <summary>One entry in an account's certificate revocation list.</summary>
public class RevokedCertificateDto
{
    public string DeviceId { get; set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the certificate bytes.</summary>
    public string CertificateFingerprint { get; set; } = null!;

    public int IdentityKeyVersion { get; set; }

    /// <summary>When it would have expired anyway, so a client can prune on its own clock rather
    /// than trusting the server to have swept.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    public string? Reason { get; set; }
    public DateTimeOffset RevokedAt { get; set; }
}
