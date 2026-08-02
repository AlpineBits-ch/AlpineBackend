using System.Security.Cryptography;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateRevokedDeviceCertificateParams
{
    public string UserId { get; init; } = null!;
    public string ClientDeviceId { get; init; } = null!;
    public byte[] Certificate { get; init; } = null!;
    public int IdentityKeyVersion { get; init; }
    public DateTimeOffset? CertificateExpiresAt { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset RevokedAt { get; init; }
}

public static class CertificateRevocationReasons
{
    public const string DeviceRemoved = "device-removed";
    public const string Reissued = "reissued";
}

/// <summary>
/// A device certificate that must no longer be honoured.
///
/// <para><b>Removing a device did not revoke anything.</b> The row went, and with it the server's
/// copy of the certificate - but a certificate is a public, self-contained, 180-day statement that
/// peers have already downloaded and can re-present. So a peer verifying a leaf against a pinned
/// account identity key would keep accepting the removed device's certificate for up to six months,
/// and the only remedy on offer was rotating the account identity key, which invalidates <i>every</i>
/// peer's pin and forces every one of them into an out-of-band re-verification. That is not a
/// revocation mechanism; it is a reset.</para>
///
/// <para>What is stored is a SHA-256 fingerprint of the certificate bytes, not the certificate.
/// A verifier only ever needs to answer "is the one in front of me on the list", the list is served
/// to any authenticated caller (as the identity key already is), and a hash keeps it from being a
/// second distribution channel for the certificates themselves.</para>
///
/// <para>Rows outlive the device deliberately. Deleting a revocation when the certificate expires
/// would be right in principle and wrong in practice - it makes the list's correctness depend on
/// clocks agreeing - so they are kept and the expiry is published alongside, letting a client prune
/// on its own reading of time.</para>
/// </summary>
public class RevokedDeviceCertificate : BaseEntity<RevokedDeviceCertificate>, IPrefixedEntity
{
    public static string Prefix { get; } = "rvcr";

    public string UserId { get; set; } = null!;

    /// <summary>The device the certificate vouched for. Unique only per user, which is why the row
    /// carries the user id too.</summary>
    public string ClientDeviceId { get; set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the certificate bytes.</summary>
    public string CertificateFingerprint { get; set; } = null!;

    /// <summary>Which account identity key version signed the revoked certificate.</summary>
    public int IdentityKeyVersion { get; set; }

    /// <summary>When the revoked certificate would have expired on its own. Lets a client drop the
    /// entry once it can no longer be presented, without the server having to sweep.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>One of <see cref="CertificateRevocationReasons"/>.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset RevokedAt { get; set; }

    public static string Fingerprint(byte[] certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate)).ToLowerInvariant();

    public static RevokedDeviceCertificate Create(CreateRevokedDeviceCertificateParams parameters)
    {
        var date = parameters.RevokedAt == default ? DateTimeOffset.UtcNow : parameters.RevokedAt;
        return new RevokedDeviceCertificate
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = parameters.UserId,
            ClientDeviceId = parameters.ClientDeviceId,
            CertificateFingerprint = Fingerprint(parameters.Certificate),
            IdentityKeyVersion = parameters.IdentityKeyVersion,
            CertificateExpiresAt = parameters.CertificateExpiresAt,
            Reason = parameters.Reason,
            RevokedAt = date,
        };
    }
}
