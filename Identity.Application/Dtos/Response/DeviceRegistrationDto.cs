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

/// <summary>
/// A device certificate as a peer needs to see it, from
/// <c>GET api/v1/users/{userId}/devices/{deviceId}/certificate</c>.
///
/// <para><see cref="IssuedAt"/> and <see cref="ExpiresAt"/> are <b>epoch seconds</b>, not
/// <c>DateTimeOffset</c>, and deliberately so: they are inside the signature. The verifier
/// reconstructs the signed payload from this response and checks it against the account identity key
/// it pinned, so the wire form here has to be the form the signer used. The upload side
/// (<c>UpdateDeviceCertificateDto</c>) takes ISO-8601 because there the server is storing a window,
/// not reproducing a payload.</para>
/// </summary>
public class DeviceCertificateDto
{
    public string DeviceId { get; set; } = null!;

    /// <summary>The MLS signing public key the certificate vouches for - the same value the device
    /// publishes at registration. What binds the certificate to a particular leaf.</summary>
    public byte[] DeviceSignatureKey { get; set; } = null!;

    public byte[] Certificate { get; set; } = null!;
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }

    /// <summary>Which account identity key version signed it, so a verifier holding a rotated key
    /// knows to look for the previous one rather than declaring a forgery.</summary>
    public int IdentityKeyVersion { get; set; }
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
