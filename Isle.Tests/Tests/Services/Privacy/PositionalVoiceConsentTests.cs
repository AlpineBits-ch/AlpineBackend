using Isle.Api.Services.Privacy;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Services.Privacy;

/// <summary>
/// Covers the T2-19 gate itself. Three answers matter: allowed when the account says so, refused
/// when it says otherwise, and refused when nothing can be resolved - the last being the one that
/// stops a stored flag from being decorative.
/// </summary>
[TestFixture]
public class PositionalVoiceConsentTests
{
    private const string UserId = "user-1";

    // ── normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task MayCaptureAsync_ConsentGranted_Allows()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);

        Assert.That(await bundle.Consent.MayCaptureAsync(UserId), Is.True);
    }

    [Test]
    public async Task MayCaptureAsync_OneUsersRefusalDoesNotAffectAnother()
    {
        var bundle = PrivacyTestFactory.Build([
            PrivacyTestFactory.WithPositionalVoice("refuser", false),
            PrivacyTestFactory.WithPositionalVoice("consenter", true),
        ]);

        Assert.Multiple(async () =>
        {
            Assert.That(await bundle.Consent.MayCaptureAsync("refuser"), Is.False);
            Assert.That(await bundle.Consent.MayCaptureAsync("consenter"), Is.True);
        });
    }

    // ── edge ──────────────────────────────────────────────────────────────

    [Test]
    public async Task MayCaptureAsync_NoUserId_Refuses()
    {
        var bundle = PrivacyTestFactory.Build();

        Assert.Multiple(async () =>
        {
            Assert.That(await bundle.Consent.MayCaptureAsync(null), Is.False);
            Assert.That(await bundle.Consent.MayCaptureAsync("   "), Is.False);
        });
    }

    // ── negative (the point) ──────────────────────────────────────────────

    [Test]
    public async Task MayCaptureAsync_ConsentWithheld_Refuses()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, false)]);

        Assert.That(await bundle.Consent.MayCaptureAsync(UserId), Is.False);
    }

    [Test]
    public async Task MayCaptureAsync_IdentityUnreachable_Refuses()
    {
        var bundle = PrivacyTestFactory.Build(lookupFails: true);

        Assert.That(await bundle.Consent.MayCaptureAsync(UserId), Is.False,
            "an unresolvable setting must not be read as permission");
    }

    [Test]
    public async Task MayCaptureAsync_EverythingBelowItIsBroken_RefusesRatherThanThrowing()
    {
        // Redis unreadable *and* Identity down: the caller is an HTTP endpoint, and a 500 here would
        // be a worse failure than a refusal the client can explain to the player.
        var bundle = PrivacyTestFactory.Build(lookupFails: true, cache: new FakeDistributedCache { Broken = true });

        Assert.That(await bundle.Consent.MayCaptureAsync(UserId), Is.False);
    }
}
