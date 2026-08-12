using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Contracts.Push;
using Messaging.Application.Services;

namespace Messaging.Tests.Push;

/// <summary>The RFC 8291 / RFC 8188 / RFC 8292 wire format.</summary>
[TestFixture]
[Category("Unit")]
public class WebPushEncryptionTests
{
    private static readonly byte[] Auth = RandomNumberGenerator.GetBytes(WebPushSubscription.AuthBytes);

    private static (ECDiffieHellman Key, byte[] Public) Subscriber()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key, Uncompressed(key.ExportParameters(false).Q));
    }

    private static byte[] Uncompressed(ECPoint q)
    {
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1 + (32 - q.X.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y.Length));
        return point;
    }

    // ══════════════════════════════════════════════════════════════════════════ Structure - RFC
    // 8188 §2.1 ══════════════════════════════════════════════════════════════════════════

    /// <summary><c>salt(16) || rs(4) || idlen(1) || keyid || record</c>.</summary>
    [Test]
    public void The_body_is_a_single_rfc8188_record()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var payload = "hello"u8;
            var body = WebPushEncryption.Encrypt(payload, peerPublic, Auth);

            Assert.That(body.Length, Is.EqualTo(16 + 4 + 1 + 65 + payload.Length + 1 + 16),
                "header + keyid + (plaintext + delimiter) + GCM tag");
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)),
                Is.EqualTo((uint)WebPushEncryption.RecordSize), "rs, big-endian");
            Assert.That(body[20], Is.EqualTo(65), "idlen names the 65-byte public key that follows");
            Assert.That(body[21], Is.EqualTo(0x04), "the keyid is an uncompressed point");
        }
    }

    /// <summary>Two messages to the same subscription must not share a salt: the salt is what separates
    /// their content keys, and a repeat would reuse an AES-GCM key/nonce pair.</summary>
    [Test]
    public void Each_message_gets_a_fresh_salt_and_ephemeral_key()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var first = WebPushEncryption.Encrypt("hello"u8, peerPublic, Auth);
            var second = WebPushEncryption.Encrypt("hello"u8, peerPublic, Auth);

            Assert.That(first[..16], Is.Not.EqualTo(second[..16]), "salt");
            Assert.That(first[21..86], Is.Not.EqualTo(second[21..86]), "ephemeral public key");
        }
    }

    /// <summary>The ceiling is enforced rather than discovered as a 413 from a push service, so a
    /// payload builder with no budget fails where it can be fixed.</summary>
    [Test]
    public void A_payload_over_the_record_budget_is_refused()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var tooBig = new byte[WebPushEncryption.MaxPayloadBytes + 1];

            Assert.Throws<ArgumentOutOfRangeException>(
                () => WebPushEncryption.Encrypt(tooBig, peerPublic, Auth));
        }
    }

    [Test]
    public void The_largest_allowed_payload_still_encrypts()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var atLimit = new byte[WebPushEncryption.MaxPayloadBytes];

            Assert.DoesNotThrow(() => WebPushEncryption.Encrypt(atLimit, peerPublic, Auth));
        }
    }

    [TestCase(64, TestName = "a point one byte short is refused")]
    [TestCase(66, TestName = "a point one byte long is refused")]
    public void A_key_that_is_not_a_p256_point_is_refused(int length)
    {
        var bogus = new byte[length];
        bogus[0] = 0x04;

        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, bogus, Auth));
    }

    /// <summary>A compressed point is a valid P-256 encoding and not one the Push API produces.
    /// Guessing at it would mean deriving a key from a point we reconstructed rather than the one the
    /// browser holds.</summary>
    [Test]
    public void A_compressed_point_is_refused()
    {
        var compressed = new byte[65];
        compressed[0] = 0x02;

        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, compressed, Auth));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Encryption - decrypted back with an independent implementation
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The end-to-end check: a subscriber holding the matching private key recovers the exact plaintext,
    /// using key derivation written out separately below from the RFC rather than shared with the
    /// implementation.
    /// </summary>
    [Test]
    public void A_subscriber_recovers_the_plaintext()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var payload = JsonSerializer.Serialize(new { type = "message", body = "hei sveis" });

            var body = WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(payload), peerPublic, Auth);

            Assert.That(Decrypt(body, peer, peerPublic, Auth), Is.EqualTo(payload));
        }
    }

    /// <summary>A payload at the byte limit is the one most likely to expose an off-by-one in the
    /// delimiter or the tag, and it is the case a long encrypted message actually hits.</summary>
    [Test]
    public void A_maximum_length_payload_round_trips()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var payload = new string('x', WebPushEncryption.MaxPayloadBytes);

            var body = WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(payload), peerPublic, Auth);

            Assert.That(Decrypt(body, peer, peerPublic, Auth), Is.EqualTo(payload));
        }
    }

    /// <summary>The auth secret is genuinely mixed in, not carried along.</summary>
    [Test]
    public void The_auth_secret_is_part_of_the_derivation()
    {
        var (peer, peerPublic) = Subscriber();
        using (peer)
        {
            var body = WebPushEncryption.Encrypt("hello"u8, peerPublic, Auth);
            var wrongAuth = RandomNumberGenerator.GetBytes(WebPushSubscription.AuthBytes);

            Assert.Throws<AuthenticationTagMismatchException>(
                () => Decrypt(body, peer, peerPublic, wrongAuth));
        }
    }

    /// <summary>A message encrypted for one subscription must not decrypt for another.</summary>
    [Test]
    public void A_message_for_one_subscription_does_not_open_for_another()
    {
        var (alice, alicePublic) = Subscriber();
        var (bob, bobPublic) = Subscriber();
        using (alice)
        using (bob)
        {
            var body = WebPushEncryption.Encrypt("for alice"u8, alicePublic, Auth);

            Assert.Throws<AuthenticationTagMismatchException>(
                () => Decrypt(body, bob, bobPublic, Auth));
        }
    }

    /// <summary>
    /// Point encoding, both ways round: the 65-byte form this code writes is the one it can also
    /// read back as a peer key, and the two sides derive the same Z.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void The_shared_secret_agrees_in_both_directions()
    {
        var (ours, oursPublic) = Subscriber();
        var (theirs, theirsPublic) = Subscriber();
        using (ours)
        using (theirs)
        {
            using var theirsAsPeer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = theirsPublic[1..33], Y = theirsPublic[33..65] },
            });
            using var oursAsPeer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = oursPublic[1..33], Y = oursPublic[33..65] },
            });

            Assert.That(ours.DeriveRawSecretAgreement(theirsAsPeer.PublicKey),
                Is.EqualTo(theirs.DeriveRawSecretAgreement(oursAsPeer.PublicKey)));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════ VAPID - RFC 8292
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void The_authorization_header_is_the_vapid_scheme()
    {
        var (header, _, _, _) = Vapid();

        Assert.That(header, Does.StartWith("vapid t="));
        Assert.That(header, Does.Contain(", k="));
    }

    /// <summary>The signature is raw <c>r||s</c>, not DER.</summary>
    [Test]
    public void The_jwt_signature_verifies_as_p1363_against_the_public_key()
    {
        var (header, publicKey, _, _) = Vapid();

        var token = header["vapid t=".Length..].Split(',')[0];
        var parts = token.Split('.');
        Assert.That(parts, Has.Length.EqualTo(3));

        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });

        Assert.That(verifier.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                WebPushSubscription.Decode(parts[2]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            Is.True);
    }

    /// <summary><c>aud</c> is the endpoint's origin, never the full URL.</summary>
    [Test]
    public void The_audience_is_the_endpoint_origin_only()
    {
        var (header, _, _, _) = Vapid(new Uri("https://fcm.googleapis.com/fcm/send/secret-path-123"));

        var claims = Claims(header);

        Assert.That(claims["aud"].ToString(), Is.EqualTo("https://fcm.googleapis.com"));
        Assert.That(header, Does.Not.Contain("secret-path-123"));
    }

    [Test]
    public void The_subject_and_expiry_are_carried()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(3);
        var (header, _, _, _) = Vapid(expires: expires);

        var claims = Claims(header);

        Assert.That(claims["sub"].ToString(), Is.EqualTo("mailto:ops@venta.test"));
        Assert.That(claims["exp"].GetInt64(), Is.EqualTo(expires.ToUnixTimeSeconds()));
    }

    /// <summary>The <c>k</c> parameter is the public key as the browser subscribed with it; a push
    /// service checks it against the subscription.</summary>
    [Test]
    public void The_k_parameter_is_the_vapid_public_key()
    {
        var (header, publicKey, _, _) = Vapid();

        Assert.That(header, Does.EndWith($", k={WebPushSubscription.Encode(publicKey)}"));
    }

    private static Dictionary<string, JsonElement> Claims(string header)
    {
        var token = header["vapid t=".Length..].Split(',')[0];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            WebPushSubscription.Decode(token.Split('.')[1]))!;
    }

    private static (string Header, byte[] PublicKey, byte[] PrivateKey, Uri Endpoint) Vapid(
        Uri? endpoint = null,
        DateTimeOffset? expires = null)
    {
        endpoint ??= new Uri("https://fcm.googleapis.com/fcm/send/abc");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        var publicKey = Uncompressed(p.Q);
        var privateKey = new byte[32];
        p.D!.CopyTo(privateKey, 32 - p.D.Length);

        var header = WebPushEncryption.BuildAuthorization(
            endpoint, "mailto:ops@venta.test", publicKey, privateKey,
            expires ?? DateTimeOffset.UtcNow.AddHours(12));

        return (header, publicKey, privateKey, endpoint);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The decryptor, written from RFC 8291 §3 in the opposite direction
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>What a browser does with the body.</summary>
    private static string Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] uaPublic, byte[] authSecret)
    {
        var salt = body[..16];
        var idlen = body[20];
        var asPublic = body[21..(21 + idlen)];
        var record = body[(21 + idlen)..];

        using var asPeer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });

        var shared = uaKey.DeriveRawSecretAgreement(asPeer.PublicKey);

        var keyInfo = new List<byte>();
        keyInfo.AddRange("WebPush: info"u8);
        keyInfo.Add(0x00);
        keyInfo.AddRange(uaPublic);
        keyInfo.AddRange(asPublic);

        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo.ToArray());

        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt,
            [.. "Content-Encoding: aes128gcm"u8, 0x00]);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt,
            [.. "Content-Encoding: nonce"u8, 0x00]);

        var ciphertext = record[..^16];
        var tag = record[^16..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        Assert.That(plaintext[^1], Is.EqualTo(0x02),
            "the last record's plaintext ends with the 0x02 delimiter (RFC 8188 §2)");

        return Encoding.UTF8.GetString(plaintext[..^1]);
    }
}
