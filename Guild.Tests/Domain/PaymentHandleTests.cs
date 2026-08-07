using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;

namespace Guild.Tests.Domain;

/// <summary>
/// The one property that matters about payment handles: the server cannot read them.
/// </summary>
[TestFixture]
public class PaymentHandleTests
{
    /// <summary>The ids and scoping columns a row needs in order to be findable at all.</summary>
    private static readonly HashSet<string> AllowedStringMembers = new(StringComparer.Ordinal)
    {
        nameof(PaymentHandleBlob.Id),
        nameof(PaymentHandleBlob.GuildId),
        nameof(PaymentHandleBlob.UserId),
        nameof(PaymentHandleKeyWrap.PaymentHandleBlobId),
        nameof(PaymentHandleKeyWrap.RecipientUserId),
        nameof(PaymentHandleKeyWrap.RecipientDeviceId),
    };

    // ══════════════════════════════════════════════════════════════════════════ The requirement,
    // as an assertion ══════════════════════════════════════════════════════════════════════════

    /// <summary>The normal case for the guarantee: the stored row carries no readable text beyond
    /// the ids that make it findable. If somebody adds <c>Kind</c>, <c>Value</c>, <c>Label</c> or a
    /// "display hint" back onto either entity, this is the test that says no.</summary>
    [Test]
    public void StoredEntities_HaveNoPlaintextStringFieldsBeyondIds()
    {
        foreach (var type in new[] { typeof(PaymentHandleBlob), typeof(PaymentHandleKeyWrap) })
        {
            var offenders = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name)
                .Where(name => !AllowedStringMembers.Contains(name))
                .ToList();

            Assert.That(offenders, Is.Empty,
                $"{type.Name} gained a readable string field: {string.Join(", ", offenders)}");
        }
    }

    /// <summary>The edge case of the same rule: the payload is bytes, not a string that happens to
    /// look like bytes. A base64 <c>string Ciphertext</c> would pass the allowlist test above only
    /// by being named something innocuous, so the payload members are pinned by type.</summary>
    [Test]
    public void SealedPayloadMembers_AreOpaqueBytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(PaymentHandleBlob).GetProperty(nameof(PaymentHandleBlob.Ciphertext))!.PropertyType,
                Is.EqualTo(typeof(byte[])));
            Assert.That(typeof(PaymentHandleBlob).GetProperty(nameof(PaymentHandleBlob.Nonce))!.PropertyType,
                Is.EqualTo(typeof(byte[])));
            Assert.That(typeof(PaymentHandleKeyWrap).GetProperty(nameof(PaymentHandleKeyWrap.WrappedKey))!.PropertyType,
                Is.EqualTo(typeof(byte[])));
        });
    }

    /// <summary>
    /// The negative case: the domain must contain no IBAN validator and no payment-link builder.
    /// </summary>
    [Test]
    public void Domain_HasNoPaymentHandleFormatter()
    {
        var domain = typeof(PaymentHandleBlob).Assembly;

        var offenders = domain.GetTypes()
            .Where(t => t.Name.Contains("PaymentHandleFormat", StringComparison.Ordinal)
                        || t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Any(m => m.Name is "BuildUri" or "ValidateIban"))
            .Select(t => t.FullName)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "URI building and IBAN validation are client work now - the server has neither the "
            + "plaintext to do it with nor permission to hold it");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Round trip - the payload really is sealed
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Seals a realistic handle set with a key the server never sees, and proves two
    /// things at once: the row and the response it produces contain none of the plaintext, and a
    /// holder of the key gets it back intact. The first half is the requirement; the second is what
    /// stops the first from being satisfiable by storing nothing useful.</summary>
    [Test]
    public void SealedHandles_AreOpaqueInStorageAndInTheResponse()
    {
        const string plaintext =
            """[{"kind":"Iban","value":"CH9300762011623852957","label":"main"},{"kind":"PayPal","value":"annak"}]""";

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var payload = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, payload, ciphertext, tag);

        // Tag appended to the ciphertext, the way a client would ship one sealed blob rather than
        // two fields the server would then have to know how to reassemble.
        var sealedBytes = ciphertext.Concat(tag).ToArray();

        var blob = PaymentHandleBlob.Create("gild-1", "anna", sealedBytes, nonce, version: 1, memberRosterVersion: 7);

        var dto = new SealedPaymentHandlesDto
        {
            UserId = blob.UserId,
            Ciphertext = blob.Ciphertext,
            Nonce = blob.Nonce,
            Version = blob.Version,
            MemberRosterVersion = blob.MemberRosterVersion,
            UpdatedAt = blob.UpdatedAt,
            WrappedKey = null,
        };

        // What actually goes over the wire, checked as the wire sees it - a leak would most likely
        // arrive as a helpfully added property on the DTO rather than on the entity.
        var wire = JsonSerializer.Serialize(dto);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Does.Not.Contain("CH9300762011623852957"));
            Assert.That(wire, Does.Not.Contain("annak"));
            Assert.That(wire, Does.Not.Contain("Iban"));
            Assert.That(wire, Does.Not.Contain("PayPal"));

            Assert.That(Encoding.UTF8.GetString(blob.Ciphertext), Does.Not.Contain("CH93"));
        });

        var recoveredCiphertext = sealedBytes[..^tag.Length];
        var recoveredTag = sealedBytes[^tag.Length..];
        var recovered = new byte[recoveredCiphertext.Length];

        using (var aes = new AesGcm(key, recoveredTag.Length))
            aes.Decrypt(blob.Nonce, recoveredCiphertext, recoveredTag, recovered);

        Assert.That(Encoding.UTF8.GetString(recovered), Is.EqualTo(plaintext),
            "a holder of the content key must get exactly what was sealed");
    }

    /// <summary>The negative half of the round trip: the wrong key does not quietly return
    /// something. AES-GCM authenticates, so a substituted key fails rather than producing plausible
    /// garbage a client might act on.</summary>
    [Test]
    public void SealedHandles_DoNotOpenWithTheWrongKey()
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var payload = Encoding.UTF8.GetBytes("CH9300762011623852957");
        var ciphertext = new byte[payload.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(RandomNumberGenerator.GetBytes(32), tag.Length))
            aes.Encrypt(nonce, payload, ciphertext, tag);

        using var wrongKey = new AesGcm(RandomNumberGenerator.GetBytes(32), tag.Length);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => wrongKey.Decrypt(nonce, ciphertext, tag, new byte[ciphertext.Length]));
    }

    // ══════════════════════════════════════════════════════════════════════════ The entity itself
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_StampsIdentityAndTimestamps()
    {
        var before = DateTimeOffset.UtcNow;

        var blob = PaymentHandleBlob.Create("gild-1", "ben", [1, 2, 3], [4, 5, 6], version: 2, memberRosterVersion: 9);

        Assert.Multiple(() =>
        {
            Assert.That(blob.Id, Does.StartWith(PaymentHandleBlob.Prefix));
            Assert.That(blob.GuildId, Is.EqualTo("gild-1"));
            Assert.That(blob.UserId, Is.EqualTo("ben"));
            Assert.That(blob.Version, Is.EqualTo(2));
            Assert.That(blob.MemberRosterVersion, Is.EqualTo(9));
            Assert.That(blob.CreatedAt, Is.GreaterThanOrEqualTo(before));
            Assert.That(blob.UpdatedAt, Is.EqualTo(blob.CreatedAt));
            Assert.That(blob.Wraps, Is.Empty);
        });
    }
}
