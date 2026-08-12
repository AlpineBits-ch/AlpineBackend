using System.Buffers.Text;

namespace Identity.Contracts.Push;

/// <summary>
/// Whether a browser's <c>PushSubscription</c> is one we can actually send to, checked at
/// registration rather than at send time.
/// </summary>
public static class WebPushSubscription
{
    /// <summary>Bytes in an uncompressed P-256 point: the <c>0x04</c> tag plus X and Y.</summary>
    public const int P256dhBytes = 65;

    /// <summary>Bytes in the auth secret (RFC 8291 §4).</summary>
    public const int AuthBytes = 16;

    /// <summary>The leading byte that says a P-256 point is uncompressed.</summary>
    private const byte UncompressedPointTag = 0x04;

    /// <summary>
    /// Null when the subscription is usable, otherwise the reason - phrased for the client developer
    /// who will read it in a 400 body.
    /// </summary>
    public static string? Validate(string? endpoint, string? p256dh, string? auth)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "endpoint is required when kind is WebPush.";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            // Refused rather than accepted-and-skipped: the server POSTs to this value, so a relative
            // or http one is a request to make an unencrypted outbound call to somewhere unknown.
            return "endpoint must be an absolute https URL.";
        }

        if (string.IsNullOrWhiteSpace(p256dh)) return "p256dh is required when kind is WebPush.";
        if (string.IsNullOrWhiteSpace(auth)) return "auth is required when kind is WebPush.";

        if (!TryDecode(p256dh, out var key) || key.Length != P256dhBytes || key[0] != UncompressedPointTag)
        {
            return $"p256dh must be a base64url uncompressed P-256 point ({P256dhBytes} bytes).";
        }

        if (!TryDecode(auth, out var secret) || secret.Length != AuthBytes)
        {
            return $"auth must be {AuthBytes} base64url-encoded bytes.";
        }

        return null;
    }

    /// <summary>Decodes the unpadded base64url the Push API produces.</summary>
    public static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
            if (!Base64Url.TryDecodeFromChars(value, buffer, out var written)) return false;

            bytes = buffer[..written];
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Decodes or throws.</summary>
    public static byte[] Decode(string value) =>
        TryDecode(value, out var bytes)
            ? bytes
            : throw new FormatException("Value is not valid base64url.");

    /// <summary>Encodes without padding, the way every field in this protocol is encoded.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);
}
