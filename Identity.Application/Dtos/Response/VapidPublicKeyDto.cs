namespace Identity.Application.Dtos.Response;

/// <summary>
/// The instance's VAPID public key, for <c>PushManager.subscribe({ applicationServerKey })</c>.
/// </summary>
public class VapidPublicKeyDto
{
    /// <summary>Base64url, unpadded, 87 characters: the uncompressed P-256 point.</summary>
    public string PublicKey { get; set; } = null!;
}
