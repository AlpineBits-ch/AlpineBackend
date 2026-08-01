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
    /// This device's certificate, signed by the <b>account</b> identity key:
    /// <c>sign(accountIdentityPrivateKey, "venta.device-cert.v1" || deviceId || deviceSignatureKey
    /// || issuedAt || expiresAt)</c>.
    ///
    /// <para>Served alongside this device's key packages so a peer can verify offline that the leaf
    /// it is about to accept really belongs to this account - with none of the account's devices
    /// online, and without trusting the server. That is what stops the external-commit recovery path
    /// from being a server-side backdoor into every group.</para>
    ///
    /// <para><b>The server validates structure and expiry, and nothing else.</b> It does not hold the
    /// account identity private half, so it cannot check the signature and must not behave as if it
    /// had - the verification that matters happens on the peer, against the identity key it pinned.
    /// A certificate the server accepted is not a certificate anyone should trust.</para>
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

    /// <summary>
    /// What this device's build understands. Stored as a set of opaque strings rather than a version
    /// number, because the questions asked of it - "can every device on this account verify a signed
    /// protection level", "what fraction of live devices carry certificates" - are about capability,
    /// and a version answers them only via a table somebody has to keep current.
    ///
    /// <para>Empty means the device never said, which is treated as "supports nothing new"
    /// everywhere it gates something. Assuming otherwise would let a silent old device hold an
    /// account in a tier it cannot actually enforce.</para>
    /// </summary>
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