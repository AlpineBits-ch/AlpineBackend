namespace Identity.Contracts.Bus.Response;

public class ConsumeMlsDeviceTokensForUserResponse
{
    public ICollection<DeviceTokenResponse> DeviceTokens { get; set; } = new List<DeviceTokenResponse>();

    /// <summary>Active devices that had no usable key package left, so the caller could not add them
    /// to the group. Surfaced rather than swallowed: these devices will never receive a Welcome and
    /// will never be able to read the conversation, and the user needs to be told that.</summary>
    public ICollection<UnreachableDeviceResponse> UnreachableDevices { get; set; } = new List<UnreachableDeviceResponse>();
}

public class UnreachableDeviceResponse
{
    public string UserId { get; set; } = null!;

    /// <summary>Client device id, matching <see cref="DeviceTokenResponse.DeviceId"/>.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;
}

public class DeviceTokenResponse
{
    public string UserId { get; set; }
    public string DeviceId { get; set; }
    public byte[] Token { get; set; }

    /// <summary>
    /// The device's account-signed certificate, served with the key package because that is the
    /// moment a peer decides whether to admit this leaf.
    ///
    /// <para>Fetching it separately would leave a window in which the server could hand over a key
    /// package and a certificate that do not belong to the same device. Null when the device has not
    /// been issued one yet - which is every device in the field until upgraded clients start
    /// self-issuing, and exactly why enforcement starts at Observe.</para>
    ///
    /// <para><b>The server did not verify this.</b> It cannot - it does not hold the account identity
    /// private half. The caller validates it against the account identity key it pinned.</para>
    /// </summary>
    public byte[]? Certificate { get; set; }

    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Which account identity key version signed the certificate, so a peer can tell a
    /// certificate under a rotated key from one it can accept against its existing pin.</summary>
    public int CertificateIdentityKeyVersion { get; set; }

    /// <summary>True when the package handed over is the reusable last-resort one rather than a
    /// single-use package. The joining leaf has no forward secrecy from that point back, and the
    /// caller is entitled to say so rather than discovering it never.</summary>
    public bool IsLastResort { get; set; }

    public override string ToString()
    {
        return $"DeviceTokenResponse: UserId={UserId}, DeviceId={DeviceId}, TokenLength={Token?.Length ?? 0}";
    }
}