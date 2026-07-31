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
/// Everything known about a QR pairing attempt, cached as JSON under <see cref="QrLoginService.PairingCacheKey"/>.
/// UserId is only populated once a mobile device has approved (or denied) it - scanning alone
/// isn't enough to know who to log in, since the desktop side must not be able to force an
/// approval before the mobile owner has actually made a decision.
/// </summary>
/// <param name="ClientDeviceId">The starting device's own id, if it has registered one. Carried
/// through so the session minted at /connect/token can be linked to that device - a QR login never
/// passes through a form where the client could send it itself.</param>
public record QrPairingState(QrPairingStatus Status, string DeviceName, DeviceType DeviceType, string? UserId,
    string? ClientDeviceId = null);

/// <summary>
/// Constants and cache-key helpers for the QR cross-device login flow. Mirrors
/// <see cref="Identity.Application.Services.Steam.SteamOpenIdService"/>'s role for the Steam
/// grant: a short-lived, single-use, Redis-backed handshake that /connect/token exchanges for
/// real tokens once approved. See QrLoginController for the start/scan/approve/status endpoints.
/// </summary>
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
