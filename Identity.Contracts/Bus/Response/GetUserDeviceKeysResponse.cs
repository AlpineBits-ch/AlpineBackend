namespace Identity.Contracts.Bus.Response;

public class GetUserDeviceKeysResponse
{
    public List<UserDeviceKeyDto> Devices { get; set; } = [];

    /// <summary>
    /// Requested user ids the handler refused to answer for because the batch was over
    /// <c>GetUserDeviceKeysRequest.MaxUserIds</c>.
    /// </summary>
    public IReadOnlyList<string> OmittedUserIds { get; set; } = [];
}

/// <summary>
/// One device's long-term public key, plus everything a caller needs to decide whether to trust it.
/// </summary>
public class UserDeviceKeyDto
{
    public string UserId { get; set; } = null!;

    /// <summary>The client device id, which is what wraps and per-device pushes are addressed to.
    /// Not the row id, which means nothing outside Identity.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;

    /// <summary>
    /// The device's long-term identity/signature public key - <c>UserDevice.IdentityPublicKey</c>,
    /// the same bytes served as <c>DeviceCertificateDto.DeviceSignatureKey</c>, and the key the
    /// device's certificate is issued over.
    /// </summary>
    public byte[] PublicKey { get; set; } = null!;

    /// <summary>True when the device holds an unexpired account-signed certificate, on the same
    /// definition <c>UserDeviceSummaryResponse.HasValidCertificate</c> uses. Deliberately says
    /// nothing about revocation - see <see cref="CertificateRevokedAt"/> - because a caller has to be
    /// able to tell "never had a certificate" from "had one and it was pulled".</summary>
    public bool HasValidCertificate { get; set; }

    /// <summary>The certificate itself, so the caller can verify <see cref="PublicKey"/> offline
    /// against the account identity key it pinned rather than taking <see cref="HasValidCertificate"/>
    /// as the server's word for it. Carried alongside the key in one response on purpose: fetched
    /// separately there is a window in which the server could pair one device's key with another's
    /// certificate.</summary>
    public byte[]? Certificate { get; set; }

    public DateTimeOffset? CertificateIssuedAt { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Which account identity key version signed the certificate.</summary>
    public int CertificateIdentityKeyVersion { get; set; }

    /// <summary>
    /// Set when this device's current certificate is on the account's revocation list.
    /// </summary>
    public DateTimeOffset? CertificateRevokedAt { get; set; }

    /// <summary>False for a device the account has marked removed but not deleted.</summary>
    public bool IsActive { get; set; }

    public DateTimeOffset? LastSeen { get; set; }
}
