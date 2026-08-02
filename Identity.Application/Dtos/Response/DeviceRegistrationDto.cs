using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// What <c>POST api/v1/devices</c> returns.
///
/// <para>Shaped as the device the client already read back, plus <see cref="IdentityRotated"/>.
/// Deliberately not a nested envelope: the shipped clients read the device fields off the top level
/// of this response, and moving them would have broken every device in the field to add one
/// boolean.</para>
/// </summary>
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

    /// <summary>
    /// True when this call replaced the device's identity public key.
    ///
    /// <para>The client must treat it as "your key packages are gone": every one on file was minted
    /// under the identity that just went away, so the server has purged them and the device has to
    /// re-run replenish before anybody can add it to a group. A client that ignores this stays
    /// invisible to every new group - which is exactly the silent failure the flag exists to
    /// end.</para>
    /// </summary>
    public bool IdentityRotated { get; set; }
}

/// <summary>Result of <c>DELETE api/v1/devices/client/{deviceId}/key-packages</c>.</summary>
public class ResetKeyPackagesResultDto
{
    public int DeletedCount { get; set; }
}

/// <summary>
/// One entry in an account's certificate revocation list.
///
/// <para>A fingerprint, not the certificate: a verifier only needs to answer "is the one I am
/// holding on this list", and serving the certificates back would make the list a distribution
/// channel for exactly the objects it exists to withdraw.</para>
/// </summary>
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
