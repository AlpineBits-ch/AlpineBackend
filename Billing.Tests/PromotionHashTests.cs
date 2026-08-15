using Billing.Application.Promotions;
using Billing.Domain.Aggregates;
using Billing.Tests.Helpers;

namespace Billing.Tests;

/// <summary>
/// The hashing that turns an identity into the only form of it this service keeps, and the refusal
/// that stops it running without a salt.
/// </summary>
[TestFixture]
public class PromotionHashTests
{
    [Test]
    public void The_same_value_hashes_the_same_way_every_time()
    {
        var hasher = PromotionFixtures.Hasher();

        Assert.That(
            hasher.Of(PromotionIdentityKind.Device, "device-a"),
            Is.EqualTo(hasher.Of(PromotionIdentityKind.Device, "device-a")));
    }

    /// <summary>What the phone control is: the same number typed two ways is the same number. Without
    /// this, a farmer defeats the whole rule with a space.</summary>
    [Test]
    public void A_phone_number_matches_however_it_was_typed()
    {
        var hasher = PromotionFixtures.Hasher();

        var canonical = hasher.Of(PromotionIdentityKind.Phone, "+41791112233");

        Assert.Multiple(() =>
        {
            Assert.That(hasher.Of(PromotionIdentityKind.Phone, "+41 79 111 22 33"), Is.EqualTo(canonical));
            Assert.That(hasher.Of(PromotionIdentityKind.Phone, "0041-79-111-22-33"),
                Is.Not.EqualTo(canonical),
                "an international prefix written as 0041 is a different string of digits, and "
                + "guessing at dialling conventions here would be worse than not matching");
        });
    }

    /// <summary>A card fingerprint is an opaque token from Stripe and is compared verbatim.
    /// Lowercasing it would collide two distinct cards, which is a refusal handed to somebody who has
    /// done nothing wrong.</summary>
    [Test]
    public void A_card_fingerprint_is_case_sensitive()
    {
        var hasher = PromotionFixtures.Hasher();

        Assert.That(
            hasher.Of(PromotionIdentityKind.Card, "Fp_ABC"),
            Is.Not.EqualTo(hasher.Of(PromotionIdentityKind.Card, "fp_abc")));
    }

    /// <summary>The kind is part of the input, so a device id and a phone number that happened to be
    /// the same string do not match each other. That false positive would refuse a legitimate trial
    /// and be nearly impossible to see in a log full of hashes.</summary>
    [Test]
    public void One_value_under_two_kinds_produces_two_hashes()
    {
        var hasher = PromotionFixtures.Hasher();

        Assert.That(
            hasher.Of(PromotionIdentityKind.Device, "12345"),
            Is.Not.EqualTo(hasher.Of(PromotionIdentityKind.Card, "12345")));
    }

    /// <summary>Two instances do not produce the same marks, which is what stops a hash lifted from one
    /// being a valid probe against another.</summary>
    [Test]
    public void Two_salts_produce_two_hashes()
    {
        Assert.That(
            PromotionFixtures.Hasher("salt-one-long-enough").Of(PromotionIdentityKind.Device, "device-a"),
            Is.Not.EqualTo(
                PromotionFixtures.Hasher("salt-two-long-enough").Of(PromotionIdentityKind.Device, "device-a")));
    }

    /// <summary>Nothing to hash is null rather than a hash of the empty string, which would match every
    /// other account that also has nothing.</summary>
    [Test]
    public void An_absent_or_meaningless_value_hashes_to_nothing()
    {
        var hasher = PromotionFixtures.Hasher();

        Assert.Multiple(() =>
        {
            Assert.That(hasher.Of(PromotionIdentityKind.Phone, null), Is.Null);
            Assert.That(hasher.Of(PromotionIdentityKind.Phone, "   "), Is.Null);
            Assert.That(hasher.Of(PromotionIdentityKind.Phone, "+--"), Is.Null,
                "punctuation with no digits in it is not a number");
            Assert.That(hasher.OfDevices(null), Is.Empty);
        });
    }

    /// <summary>An account that reports one device twice would otherwise produce two identical marks,
    /// and the unique index would refuse the redemption.</summary>
    [Test]
    public void A_repeated_device_hashes_once()
    {
        var hashes = PromotionFixtures.Hasher()
            .OfDevices(["device-a", "device-a", " device-a ", "device-b"]);

        Assert.That(hashes, Has.Count.EqualTo(2));
    }

    // ── The salt is not optional ─────────────────────────────────────────────

    /// <summary>
    /// <b>The wave-6 rule, carried over.</b> A default is a value used when somebody forgets, and here
    /// forgetting means every hash on the instance is computed with a public constant - which turns the
    /// marks table into a lookup service for whether a given number has an account here.
    /// </summary>
    [Test]
    public void There_is_no_compiled_in_salt()
    {
        var options = new PromotionOptions { HashSalt = string.Empty };

        Assert.Multiple(() =>
        {
            Assert.That(options.IsConfigured, Is.False);
            Assert.That(() => options.EnsureConfigured(),
                Throws.InstanceOf<InvalidOperationException>()
                    .With.Message.Contains(PromotionOptions.SaltVariable));
        });
    }

    [Test]
    public void A_salt_short_enough_to_be_a_word_is_refused()
    {
        var options = new PromotionOptions { HashSalt = "hunter2" };

        Assert.Multiple(() =>
        {
            Assert.That(options.IsConfigured, Is.False);
            Assert.That(() => options.EnsureConfigured(), Throws.InstanceOf<InvalidOperationException>());
        });
    }

    /// <summary>Hashing with no salt throws rather than producing an unsalted mark.</summary>
    [Test]
    public void Hashing_without_a_salt_refuses_rather_than_falling_back()
    {
        var hasher = new PromotionHasher(
            Microsoft.Extensions.Options.Options.Create(new PromotionOptions { HashSalt = string.Empty }));

        Assert.That(
            () => hasher.Of(PromotionIdentityKind.Device, "device-a"),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void A_configured_salt_is_accepted()
    {
        var options = PromotionFixtures.Options();

        Assert.Multiple(() =>
        {
            Assert.That(options.IsConfigured, Is.True);
            Assert.That(() => options.EnsureConfigured(), Throws.Nothing);
        });
    }
}
