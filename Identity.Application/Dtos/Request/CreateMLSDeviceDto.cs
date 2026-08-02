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
    /// Refreshed on every launch and merged with what is already recorded - a re-registration can
    /// add to a device's declared support but never take it away, because erasing it is a way to
    /// hold the whole account out of <c>VerifiedDevices</c> for the price of a session token.</summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>Account password.</summary>
    public string? Password { get; set; }
}

/// <summary>Claims an existing device row for the calling session.</summary>
public class BindSessionDto
{
    public string? Password { get; set; }
}

public class BindSessionResultDto
{
    /// <summary>The client device id this session now acts as.</summary>
    public string DeviceId { get; set; } = null!;

    /// <summary>False when the session was already bound to this same device - the request changed
    /// nothing. Distinguished from a fresh bind so a client retrying after a lost response can tell
    /// which of its attempts landed.</summary>
    public bool Bound { get; set; }
}

/// <summary>Reissues a device certificate without re-registering the device.</summary>
public class UpdateDeviceCertificateDto
{
    public byte[] Certificate { get; set; } = null!;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int IdentityKeyVersion { get; set; }
}