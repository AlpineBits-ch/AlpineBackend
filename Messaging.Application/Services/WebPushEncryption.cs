using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Contracts.Push;

namespace Messaging.Application.Services;

/// <summary>
/// The cryptography of a Web Push message: RFC 8291 key agreement, RFC 8188 <c>aes128gcm</c>
/// framing, and the RFC 8292 VAPID authorization header.
/// </summary>
public static class WebPushEncryption
{
    /// <summary>Content coding of the body, and the value of the <c>Content-Encoding</c> header.</summary>
    public const string ContentEncoding = "aes128gcm";

    /// <summary>Bytes of AES-128 key material.</summary>
    private const int KeyLength = 16;

    /// <summary>Bytes of GCM nonce.</summary>
    private const int NonceLength = 12;

    /// <summary>Bytes of GCM authentication tag.</summary>
    private const int TagLength = 16;

    /// <summary>Bytes of salt in the RFC 8188 header. Also the HKDF salt.</summary>
    public const int SaltLength = 16;

    /// <summary>The single record size written into the header.</summary>
    public const int RecordSize = 4096;

    /// <summary>Largest plaintext that fits one record.</summary>
    public const int MaxPayloadBytes = RecordSize - TagLength - 1;

    /// <summary>The delimiter that ends the last record's plaintext (RFC 8188 §2).</summary>
    private const byte LastRecordDelimiter = 0x02;

    private static readonly byte[] KeyInfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    /// <summary>
    /// Encrypts one payload to one subscription and returns the complete RFC 8188 body.
    /// </summary>
    /// <param name="payload">Plaintext, at most <see cref="MaxPayloadBytes"/> bytes.</param>
    /// <param name="uaPublicKey">The subscription's <c>p256dh</c>, decoded: 65 bytes, uncompressed.</param>
    /// <param name="authSecret">The subscription's <c>auth</c>, decoded: 16 bytes.</param>
    /// <param name="salt">Test seam. Null generates a fresh 16-byte salt, which is what production
    /// does; the RFC's vectors need the published one.</param>
    /// <param name="ephemeralKey">Test seam, for the same reason. Null generates a fresh keypair.</param>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> payload,
        byte[] uaPublicKey,
        byte[] authSecret,
        byte[]? salt = null,
        ECDiffieHellman? ephemeralKey = null)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"A Web Push payload is at most {MaxPayloadBytes} bytes; got {payload.Length}.");
        }

        salt ??= RandomNumberGenerator.GetBytes(SaltLength);

        var ownsKey = ephemeralKey is null;
        var local = ephemeralKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var asPublicKey = ExportUncompressed(local);

            using var peer = ImportUncompressed(uaPublicKey);

            // The raw shared Z, not the hashed agreement: RFC 8291 §3.3 feeds Z straight into HKDF, so
            // DeriveKeyFromHash would silently derive a different - and undecryptable - key.
            var sharedSecret = local.DeriveRawSecretAgreement(peer.PublicKey);

            // Step one, RFC 8291 §3.4: bind the shared secret to both public keys, salted with the
            // subscription's auth secret.
            var keyInfo = Concat(KeyInfoPrefix, uaPublicKey, asPublicKey);
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

            // Step two, RFC 8188 §2.2: the per-message salt separates the content key from the IKM, so
            // two messages to the same subscription never reuse a key/nonce pair.
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, salt, CekInfo);
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, NonceLength, salt, NonceInfo);

            var plaintext = new byte[payload.Length + 1];
            payload.CopyTo(plaintext);
            plaintext[^1] = LastRecordDelimiter;

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(cek, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Header: salt(16) || rs(4, big-endian) || idlen(1) || keyid, then the record.
            var body = new byte[SaltLength + 4 + 1 + asPublicKey.Length + ciphertext.Length + tag.Length];
            var at = 0;
            salt.CopyTo(body, at);
            at += SaltLength;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(at), RecordSize);
            at += 4;
            body[at++] = (byte)asPublicKey.Length;
            asPublicKey.CopyTo(body, at);
            at += asPublicKey.Length;
            ciphertext.CopyTo(body, at);
            at += ciphertext.Length;
            tag.CopyTo(body, at);

            CryptographicOperations.ZeroMemory(cek);
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(sharedSecret);

            return body;
        }
        finally
        {
            if (ownsKey) local.Dispose();
        }
    }

    /// <summary>
    /// The <c>Authorization</c> header value for one push: <c>vapid t=&lt;JWT&gt;, k=&lt;pubkey&gt;</c>
    /// (RFC 8292 §3).
    /// </summary>
    /// <param name="endpoint">The subscription endpoint. Only its <b>origin</b> goes into the
    /// <c>aud</c> claim - a full URL there is rejected by some push services, and the path is a secret
    /// that has no business in a token.</param>
    /// <param name="subject">A <c>mailto:</c> or <c>https:</c> URI identifying the operator.</param>
    /// <param name="publicKey">VAPID public key, decoded: 65 bytes uncompressed.</param>
    /// <param name="privateKey">VAPID private scalar, decoded: 32 bytes.</param>
    /// <param name="expires">JWT <c>exp</c>. At most 24 hours ahead, per RFC 8292.</param>
    public static string BuildAuthorization(
        Uri endpoint,
        string subject,
        byte[] publicKey,
        byte[] privateKey,
        DateTimeOffset expires)
    {
        var header = WebPushSubscription.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);

        var claims = WebPushSubscription.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            // Scheme and host only.
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            // The public half is required to import a private key on Windows CNG.
            Q = ToPoint(publicKey),
        });

        // Raw r||s, not the DER encoding SignData produces by default.
        var signature = ecdsa.SignData(signingInput, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"vapid t={header}.{claims}.{WebPushSubscription.Encode(signature)}, k={WebPushSubscription.Encode(publicKey)}";
    }

    /// <summary>The local public key as the 65-byte uncompressed point the wire format wants.</summary>
    private static byte[] ExportUncompressed(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var point = new byte[1 + 32 + 32];
        point[0] = 0x04;
        // Left-padded into fixed 32-byte fields, and no test can reach the padding. .NET normalises
        // a named curve's coordinates to the curve's field width on export, so q.X and q.Y are
        // always 32 bytes here and the offset is always 1 and 33 (checked over 20,000 generated
        // keys: zero short coordinates).
        q.X!.CopyTo(point, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y.Length));
        return point;
    }

    private static ECDiffieHellman ImportUncompressed(byte[] point) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = ToPoint(point),
        });

    private static ECPoint ToPoint(byte[] uncompressed)
    {
        if (uncompressed.Length != WebPushSubscription.P256dhBytes || uncompressed[0] != 0x04)
        {
            throw new ArgumentException(
                $"Expected a {WebPushSubscription.P256dhBytes}-byte uncompressed P-256 point.",
                nameof(uncompressed));
        }

        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }
}
