using Identity.Domain.Aggregates;
using Identity.Domain.Enums;

namespace Identity.Tests.Domain;

/// <summary>
/// T1-9: what <see cref="ApplicationUser.Tombstone"/> leaves behind.
///
/// <para>The bug these pin was an omission, not a mistake: the tombstone scrubbed name, email, phone,
/// bio, password and Steam id, and left the date of birth plus the three age-verification timestamps
/// standing on the row. A birth date is one of the highest-value re-identification keys there is, and
/// "self-declared on this date, government-ID verified on that date" is a profile of a person on a
/// row whose entire purpose is that the person has been erased.</para>
/// </summary>
[TestFixture]
public class TombstoneAgeDataTests
{
    private static ApplicationUser NewUser(int ageYears) =>
        ApplicationUser.Create(new CreateUserParams
        {
            Email = $"tomb-{Guid.NewGuid():N}@example.com",
            Username = $"tomb{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-ageYears)),
        });

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public void Tombstone_ClearsTheBirthDateAndTheWholeAgeVerificationObject()
    {
        var user = NewUser(25);

        // Everything the old tombstone left in place, present before the call.
        Assert.Multiple(() =>
        {
            Assert.That(user.BirthDate, Is.Not.EqualTo(default(DateOnly)));
            Assert.That(user.AgeVerification.BirthDate, Is.Not.EqualTo(default(DateOnly)));
            Assert.That(user.AgeVerification.SelfDeclarationCompletedAt, Is.Not.Null);
            Assert.That(user.AgeVerification.Level, Is.EqualTo(AgeVertificationLevel.SelfDeclaration));
        });

        user.Tombstone();

        Assert.Multiple(() =>
        {
            Assert.That(user.BirthDate, Is.EqualTo(default(DateOnly)),
                "the birth date on the user row must not survive the purge");
            Assert.That(user.AgeVerification.BirthDate, Is.EqualTo(default(DateOnly)),
                "the birth date inside the age-verification object must not survive either - it is "
                + "the same date, stored twice");
            Assert.That(user.AgeVerification.Level, Is.EqualTo(AgeVertificationLevel.None));
            Assert.That(user.AgeVerification.SelfDeclarationCompletedAt, Is.Null);
            Assert.That(user.AgeVerification.AiEstimationCompletedAt, Is.Null);
            Assert.That(user.AgeVerification.GovermentIdCompletedAt, Is.Null);
        });
    }

    [Test]
    public void Tombstone_StillScrubsEverythingItAlreadyDid()
    {
        // The regression guard: adding the age scrub must not have cost any of the original erasure.
        var user = NewUser(30);
        user.SetPasswordHash("hashed");
        user.SteamId = "76561198000000000";
        user.Bio = "hello";
        user.JsonSettings = """{"theme":"dark"}""";

        user.Tombstone();

        Assert.Multiple(() =>
        {
            Assert.That(user.Email, Is.Null);
            Assert.That(user.NormalizedEmail, Is.Null);
            Assert.That(user.PhoneNumber, Is.Null);
            Assert.That(user.Bio, Is.Null);
            Assert.That(user.PasswordHash, Is.Null);
            Assert.That(user.SteamId, Is.Null);
            Assert.That(user.JsonSettings, Is.EqualTo("{}"));
            Assert.That(user.UserName, Does.StartWith("Deleted User"));
            Assert.That(user.Status, Is.EqualTo(UserStatus.Deleted));
        });
    }

    [Test]
    public void Tombstone_RetainsOnlyTheNonIdentifyingAdultBoolean()
    {
        var adult = NewUser(30);
        var minor = NewUser(14);

        adult.Tombstone();
        minor.Tombstone();

        Assert.Multiple(() =>
        {
            Assert.That(adult.WasVerifiedAdult, Is.True);
            Assert.That(minor.WasVerifiedAdult, Is.False);
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public void Tombstone_WithRetentionDisabled_KeepsNothingAtAll()
    {
        var user = NewUser(30);

        user.Tombstone(new TombstoneOptions { RetainWasVerifiedAdult = false });

        Assert.That(user.WasVerifiedAdult, Is.Null,
            "a deployment that does not want even the boolean must end up with nothing");
    }

    [Test]
    public void Tombstone_HonoursAConfiguredAgeOfMajority()
    {
        var user = NewUser(20);

        user.Tombstone(new TombstoneOptions { AgeOfMajority = 21 });

        Assert.That(user.WasVerifiedAdult, Is.False,
            "the retained boolean has to mean 'adult under the rule this deployment applies', not "
            + "'adult under 18'");
    }

    [Test]
    public void Tombstone_BotAccountWithNoBirthDate_RecordsNothingRatherThanFalse()
    {
        // CreateBot leaves AgeVerification with no birth date at all. "We have no evidence either
        // way" and "we have evidence they were a minor" are different claims, and only one is true.
        var bot = ApplicationUser.CreateBot("user_bot123456", "Test Bot");

        bot.Tombstone();

        Assert.Multiple(() =>
        {
            Assert.That(bot.WasVerifiedAdult, Is.Null);
            Assert.That(bot.AgeVerification.Level, Is.EqualTo(AgeVertificationLevel.None));
        });
    }

    [Test]
    public void Tombstone_CalledTwice_DoesNotOverwriteTheRetainedBoolean()
    {
        var user = NewUser(30);

        user.Tombstone();
        var afterFirst = user.WasVerifiedAdult;

        // Redelivery of PurgeUserDataCommand. By this point the birth date is gone, so a second run
        // that recomputed the flag would compute it from nothing and quietly clear it.
        user.Tombstone();

        Assert.Multiple(() =>
        {
            Assert.That(afterFirst, Is.True);
            Assert.That(user.WasVerifiedAdult, Is.True,
                "an idempotent purge must not degrade the record it already produced");
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public void IsMinorAt_TheDayBeforeAndTheDayOfTheEighteenthBirthday_Flips()
    {
        var birthday = new DateOnly(2008, 8, 4);
        var user = NewUser(30);
        user.AgeVerification.BirthDate = birthday;

        var dayBefore = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var theDay = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(user.IsMinorAt(dayBefore, 18), Is.True);
            Assert.That(user.IsMinorAt(theDay, 18), Is.False,
                "the restrictions have to lift on the birthday itself, with nothing having to run");
        });
    }

    [Test]
    public void IsMinorAt_WithNoBirthDate_IsFalse()
    {
        // Never true by default. A missing birth date is not evidence of youth, and treating it as
        // such would apply child protections to every bot and every already-purged account.
        var bot = ApplicationUser.CreateBot("user_bot654321", "Test Bot");

        Assert.That(bot.IsMinorAt(DateTimeOffset.UtcNow, 18), Is.False);
    }
}
