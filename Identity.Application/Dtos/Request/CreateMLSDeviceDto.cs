using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

public class CreateMLSDeviceDto
{
    public string DeviceName { get; set; }
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; }
    public string ClientDeviceId { get; set; }

    /// <summary>
    /// Optional certificate signed by the account identity key. See
    /// <see cref="Identity.Domain.Entities.UserDevice.Certificate"/>.
    ///
    /// <para>Optional because a device registering before the account has an identity key - or one
    /// mid-restore - still has to be able to register. Without a certificate it simply cannot be
    /// validated offline by a peer, which is a real cost the client should surface rather than
    /// something to block registration over.</para>
    /// </summary>
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

    /// <summary>
    /// Account password. Required only when this registration <i>rotates</i> the identity key of a
    /// device the calling session neither is nor can become, because that purges the target device's
    /// key packages and strands it.
    ///
    /// <para>"Can become" is the part that matters for an upgrading install: a session older than the
    /// device row has no device, and claims the row on this call provided nobody has ever been it -
    /// see <c>SessionDeviceResolver.TryClaimAsync</c>. So the ordinary
    /// placeholder-key-becomes-real-signing-key rotation costs nothing, while rotating a device some
    /// other session has already claimed, or that holds an encrypted backup, still costs the
    /// password.</para>
    ///
    /// <para>Note this is <i>not</i> the same rule as the account identity key at
    /// <c>PUT api/v1/users/identity-key</c>, which requires the password unconditionally, first
    /// publication included (contract §J.2, as corrected). A device identity key names one handset; an
    /// account identity key is what every peer pins.</para>
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>Claims an existing device row for the calling session. See
/// <c>MlsDeviceEndpoint.BindSessionToDevice</c> - the password is the only thing that makes this
/// distinguishable from the attack it resembles.</summary>
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

/// <summary>Reissues a device certificate without re-registering the device. Any device holding the
/// account identity key can issue for any of the account's devices, which is what makes expiry
/// cheap.</summary>
public class UpdateDeviceCertificateDto
{
    public byte[] Certificate { get; set; } = null!;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int IdentityKeyVersion { get; set; }
}