using Identity.Domain.Enums;

namespace Identity.Application.Services.Qr;

public enum QrPairingStatus
{
    Pending,
    Scanned,
    Approved,
    Denied,
}

/// <summary>
/// Everything known about a QR pairing attempt, cached as JSON under <see
/// cref="QrLoginService.PairingCacheKey"/>.
/// </summary>
/// <param name="ClientDeviceId">The starting device's own id, if it has registered one.</param>
public record QrPairingState(QrPairingStatus Status, string DeviceName, DeviceType DeviceType, string? UserId,
    string? ClientDeviceId = null);

/// <summary>Constants and cache-key helpers for the QR cross-device login flow.</summary>
public static class QrLoginService
{
    /// <summary>Custom OpenIddict grant type used to exchange an approved QR pairing code for tokens.</summary>
    public const string QrGrantType = "urn:echo:params:oauth:grant-type:qr_login";

    /// <summary>Token endpoint parameter carrying the pairing code.</summary>
    public const string CodeParameter = "qr_code";

    /// <summary>How long a pairing code stays valid if nobody scans/approves it.</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(3);

    /// <summary>Redis key holding the current QrPairingState for a pairing code.</summary>
    public static string PairingCacheKey(string code) => $"qr_login:{code}";
}
