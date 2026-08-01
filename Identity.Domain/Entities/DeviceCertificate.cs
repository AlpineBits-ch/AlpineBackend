namespace Identity.Domain.Entities;

/// <summary>
/// The rules the server can actually enforce about a device certificate, and a name for the ones it
/// cannot.
///
/// <para><b>The server cannot verify a device certificate.</b> It is signed by the account identity
/// key, whose private half is wrapped under the recovery key and never leaves the client - which is
/// the entire reason the certificate is worth anything. So what happens here is structural: is there
/// a signature, is it a plausible length, is the validity window sane and unexpired. A certificate
/// that passes these checks has proven nothing except that it is well formed; the peer validates it
/// against the account identity key it pinned, and that is the check that counts.</para>
///
/// <para>Rejecting the malformed ones here is still worth doing: it turns a certificate that would
/// have failed silently on some peer weeks later into a 400 at the moment the client uploaded
/// it.</para>
/// </summary>
public static class DeviceCertificate
{
    /// <summary>Longest validity window a certificate may claim. Short enough that a device nobody
    /// is willing to reissue for stops being verifiable, long enough that reissue is not a chore.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(180);

    /// <summary>Ed25519 signatures are 64 bytes; the certificate is that plus the signed preamble.
    /// A generous floor, purely to reject empty or obviously truncated uploads.</summary>
    public const int MinLength = 64;

    public const int MaxLength = 4096;

    public static string? Validate(byte[]? certificate, DateTimeOffset? issuedAt, DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        if (certificate is null || certificate.Length == 0) return null; // absent is allowed; invalid is not

        if (certificate.Length is < MinLength or > MaxLength)
            return $"Device certificate must be between {MinLength} and {MaxLength} bytes.";

        if (issuedAt is null || expiresAt is null)
            return "Device certificate requires both issuedAt and expiresAt.";

        if (expiresAt <= issuedAt)
            return "Device certificate expiresAt must be after issuedAt.";

        if (expiresAt - issuedAt > MaxLifetime)
            return $"Device certificate lifetime must not exceed {MaxLifetime.TotalDays:0} days.";

        // Already-expired certificates are refused at upload rather than stored and served: serving
        // one guarantees a peer-side rejection and, under the external-commit path, a removal
        // proposal against a device that did nothing wrong except forget to reissue.
        if (expiresAt <= now)
            return "Device certificate has already expired; reissue it before uploading.";

        return null;
    }
}
