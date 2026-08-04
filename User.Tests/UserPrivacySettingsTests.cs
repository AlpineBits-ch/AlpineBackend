using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace User.Tests;

/// <summary>
/// The privacy record a new account is born with.
///
/// <para>Every assertion here is about a <i>default</i>, and defaults are the whole substance of this
/// entity: a consent flag that starts true is consent nobody gave, and a contactability policy that
/// starts permissive is a control the user has to find and switch off before it protects them. So
/// the negative cases - "this is not true" - carry more weight than the positive ones.</para>
///
/// <para>The minting assertions exist for the same reason
/// <see cref="ApplicationUserBotTests.CreateBot_PopulatesAgeVerificationAndUserPreferences_SoSaveDoesNotViolateRequiredOwnedEntities"/>
/// does: a related entity a factory forgets to populate is not a compile error, it is a
/// <c>SaveChanges</c> failure on a code path nobody runs until production.</para>
/// </summary>
public class UserPrivacySettingsTests
{
    private static CreateUserParams Params() => new()
    {
        Email = "someone@example.com",
        PhoneNumber = "+41000000000",
        Username = "someone",
        BirthDate = new DateOnly(2000, 1, 1),
    };

    // ── minting ─────────────────────────────────────────────────────────────

    [Test]
    public void Create_MintsAPrivacySettingsRowPointingAtTheNewUser()
    {
        var user = ApplicationUser.Create(Params());

        Assert.Multiple(() =>
        {
            Assert.That(user.UserPrivacySettings, Is.Not.Null);
            Assert.That(user.UserPrivacySettings!.Id, Does.StartWith("upvs_"));
            Assert.That(user.UserPrivacySettings.UserId, Is.EqualTo(user.Id),
                "the settings row has to point back at the account it was minted for, or the "
                + "cross-service lookup finds nothing and every consumer falls through to defaults");
        });
    }

    [Test]
    public void CreateBot_AlsoMintsAPrivacySettingsRow()
    {
        // A bot is a real account with real reachability - "who may DM this bot" has an answer - and
        // a missing row would make every policy resolution against it take the fail-closed path.
        var bot = ApplicationUser.CreateBot("user_bot1", "Test Bot");

        Assert.Multiple(() =>
        {
            Assert.That(bot.UserPrivacySettings, Is.Not.Null);
            Assert.That(bot.UserPrivacySettings!.UserId, Is.EqualTo("user_bot1"));
        });
    }

    [Test]
    public void Create_StillMintsTheLegacyPreferencesBlock()
    {
        // Additive, not a replacement: v1 clients read userPreferences off GET /users/self, and the
        // AddUserPrivacySettings migration deliberately copied rather than moved.
        var user = ApplicationUser.Create(Params());

        Assert.Multiple(() =>
        {
            Assert.That(user.UserPreferences, Is.Not.Null);
            Assert.That(user.UserPreferences.Id, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Create_MintsDistinctSettingsRowsForDistinctUsers()
    {
        var first = ApplicationUser.Create(Params());
        var second = ApplicationUser.Create(Params());

        Assert.Multiple(() =>
        {
            Assert.That(first.UserPrivacySettings!.Id, Is.Not.EqualTo(second.UserPrivacySettings!.Id));
            Assert.That(first.UserPrivacySettings.UserId, Is.Not.EqualTo(second.UserPrivacySettings.UserId));
        });
    }

    // ── consent defaults (the negative cases that matter) ───────────────────

    [Test]
    public void NewSettings_GrantNoConsentAtAll()
    {
        var settings = ApplicationUser.Create(Params()).UserPrivacySettings!;

        Assert.Multiple(() =>
        {
            Assert.That(settings.AllowDataCollection, Is.False,
                "consent is opt-in; a row minted before anyone was asked must not represent a yes");
            Assert.That(settings.AllowPersonalization, Is.False);
            Assert.That(settings.AllowVoiceRecordingInClips, Is.False);
        });
    }

    [Test]
    public void NewBotSettings_GrantNoConsentEither()
    {
        var settings = ApplicationUser.CreateBot("user_bot2", "Test Bot").UserPrivacySettings!;

        Assert.Multiple(() =>
        {
            Assert.That(settings.AllowDataCollection, Is.False);
            Assert.That(settings.AllowPersonalization, Is.False);
            Assert.That(settings.AllowVoiceRecordingInClips, Is.False);
        });
    }

    // ── contactability and discoverability defaults ────────────────────────

    [Test]
    public void NewSettings_DefaultDirectMessagePolicyIsFriends_NotEveryone()
    {
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.That(settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
        Assert.That(settings.DirectMessagePolicy, Is.Not.EqualTo(DirectMessagePolicy.Everyone),
            "the fail-closed default for DMs is Friends - see the cross-cutting rules in the spec");
    }

    [Test]
    public void NewSettings_DefaultFriendRequestPolicyIsEveryone()
    {
        // Matches Discord, and is the one deliberately permissive default: the block list is the
        // escape hatch, and a stricter default makes the product unable to do the thing it is for.
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.That(settings.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Everyone));
    }

    [Test]
    public void NewSettings_AreDiscoverableByUsernameOnly()
    {
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(settings.DiscoverableByUsername, Is.True,
                "exact-username lookup is how people find each other here");
            Assert.That(settings.DiscoverableByEmail, Is.False,
                "email lookup exposes an identifier the user gave for authentication, not for search");
            Assert.That(settings.DiscoverableByPhone, Is.False);
        });
    }

    [Test]
    public void NewSettings_HideTheBirthdayAndShowProfileFieldsToFriendsOnly()
    {
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(settings.BirthdayVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(settings.MutualServersVisibility, Is.EqualTo(Visibility.Friends));
            Assert.That(settings.MutualFriendsVisibility, Is.EqualTo(Visibility.Friends));
            Assert.That(settings.ConnectionsVisibility, Is.EqualTo(Visibility.Friends));
        });
    }

    [Test]
    public void NewSettings_KeepMessagesForeverAndFilterUnknownSenders()
    {
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(settings.DmRetentionDays, Is.Null, "null is 'keep forever'; retention is opt-in");
            Assert.That(settings.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.UnknownSenders));
            Assert.That(settings.HidePushContent, Is.False);
            Assert.That(settings.ShareActivity, Is.True);
            Assert.That(settings.AllowPositionalVoiceCapture, Is.True);
            Assert.That(settings.SendReadReceipts, Is.True);
            Assert.That(settings.SendTypingIndicators, Is.True);
        });
    }

    [Test]
    public void NewSettings_StartAtVersionZero()
    {
        var settings = UserPrivacySettings.CreateDefault("user_x", DateTimeOffset.UtcNow);

        Assert.That(settings.Version, Is.Zero,
            "the first write has to be distinguishable from never having been written");
    }

    [Test]
    public void CreateDefault_StampsBothTimestampsFromTheSuppliedClock()
    {
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        var settings = UserPrivacySettings.CreateDefault("user_x", now);

        Assert.Multiple(() =>
        {
            Assert.That(settings.CreatedAt, Is.EqualTo(now));
            Assert.That(settings.UpdatedAt, Is.EqualTo(now));
        });
    }
}
