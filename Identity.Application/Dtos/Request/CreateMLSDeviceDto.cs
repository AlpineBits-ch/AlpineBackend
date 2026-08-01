using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

public class CreateMLSDeviceDto
{
    public string DeviceName { get; set; }
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; }
    public string ClientDeviceId { get; set; }

    /// <summary>Optional certificate signed by the account identity key.</summary>
    public byte[]? DeviceCertificate { get; set; }

    public DateTimeOffset? CertificateIssuedAt { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Which account identity key version signed the certificate.</summary>
    public int CertificateIdentityKeyVersion { get; set; }

    /// <summary>What this build understands, e.g. <c>mls.device-cert.v1</c>,
    /// <c>mls.join-request.conversation.v1</c>, <c>mls.protection-level.v1</c>, <c>mls.backup.v1</c>.
    /// Refreshed on every launch. Null leaves whatever was previously recorded in place, so a
    /// partial re-registration cannot erase a device's declared support.</summary>
    public List<string>? Capabilities { get; set; }
}

/// <summary>Reissues a device certificate without re-registering the device.</summary>
public class UpdateDeviceCertificateDto
{
    public byte[] Certificate { get; set; } = null!;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int IdentityKeyVersion { get; set; }
}