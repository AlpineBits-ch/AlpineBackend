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

/// <summary>A device certificate that must no longer be honoured.</summary>
public class RevokedDeviceCertificate : BaseEntity<RevokedDeviceCertificate>, IPrefixedEntity
{
    public static string Prefix { get; } = "rvcr";

    public string UserId { get; set; } = null!;

    /// <summary>The device the certificate vouched for.</summary>
    public string ClientDeviceId { get; set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the certificate bytes.</summary>
    public string CertificateFingerprint { get; set; } = null!;

    /// <summary>Which account identity key version signed the revoked certificate.</summary>
    public int IdentityKeyVersion { get; set; }

    /// <summary>When the revoked certificate would have expired on its own.</summary>
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
