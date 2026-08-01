using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;


public class CreateUserDeviceParams
{
    public string ClientDeviceId { get; set; }
    public string DeviceName { get; set; }
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; }
    public string UserId { get; set; }
}

public class UserDevice  : BaseEntity<UserDevice>, IPrefixedEntity
{
    public static string Prefix { get; } = "udev";
    
    public string ClientDeviceId { get; init; }
    
    public string UserId { get; set; } = null!;
    public string DeviceName { get; set; } = null!;      // "Alice's MacBook"
    public DeviceType DeviceType { get; set; }            // DESKTOP, MOBILE, WEB
    public byte[] IdentityPublicKey { get; set; } = null!; // MLS public identity key
    // private never leaves device
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// This device's certificate, signed by the account identity key:
    /// <c>sign(accountIdentityPrivateKey, "venta.device-cert.v1" || deviceId || deviceSignatureKey
    /// || issuedAt || expiresAt)</c>.
    /// </summary>
    public byte[]? Certificate { get; set; }

    public DateTimeOffset? CertificateIssuedAt { get; set; }

    /// <summary>Certificates expire so a device that was compromised and quietly kept stops being
    /// verifiable once no live device is willing to reissue for it.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Which <see cref="Aggregates.ApplicationUser.AccountIdentityKeyVersion"/> signed it.
    /// A certificate under a superseded identity key is not merely old - the peer's pinning has
    /// moved, and it must be re-verified rather than accepted.</summary>
    public int CertificateIdentityKeyVersion { get; set; }

    public bool HasValidCertificateAt(DateTimeOffset now) =>
        Certificate is { Length: > 0 } && CertificateExpiresAt > now;

    /// <summary>What this device's build understands.</summary>
    public List<string> Capabilities { get; set; } = [];

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public ICollection<UserKeyPackage> KeyPackages { get; set; } = [];

    /// <summary>Retained backup versions, newest first is not guaranteed - order explicitly. Was a
    /// single blob; a backup that overwrote itself meant one truncated upload destroyed the only
    /// copy of this device's signing key. See <see cref="UserDeviceBackup.RetainedVersions"/>.</summary>
    public virtual ICollection<UserDeviceBackup> Backups { get; set; } = [];
    public ICollection<UserPushToken> PushTokens { get; set; } = [];
    
    public static UserDevice Create(CreateUserDeviceParams createUserDeviceParams)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        return new UserDevice
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            ClientDeviceId = createUserDeviceParams.ClientDeviceId,
            DeviceName = createUserDeviceParams.DeviceName,
            DeviceType = createUserDeviceParams.DeviceType,
            IdentityPublicKey = createUserDeviceParams.IdentityPublicKey,
            UserId = createUserDeviceParams.UserId,
        };
    }

}