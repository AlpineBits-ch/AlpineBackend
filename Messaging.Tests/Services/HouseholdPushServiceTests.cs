using Guild.Contracts;
using Messaging.Application.Services;

namespace Messaging.Tests.Services;

/// <summary>The shape of a household push on the wire.</summary>
[TestFixture]
public class HouseholdPushServiceTests
{
    private const string Token = "tok-1";
    private const string UserId = "anna";

    private static HouseholdPushPayload Payload(
        string? titleKey = null, string[]? titleArgs = null,
        string? bodyKey = HouseholdLocKeys.PantryLowListedBody, string[]? bodyArgs = null,
        IReadOnlySet<string>? hideFor = null) =>
        new()
        {
            GuildId = "guild-1",
            ChannelId = "chan-pantry",
            Kind = "pantry.low",
            TargetId = "pitm-1",
            Title = "Milk",
            Body = "Running low, so it's gone on Shopping.",
            TitleLocKey = titleKey,
            TitleLocArgs = titleArgs ?? [],
            BodyLocKey = bodyKey,
            BodyLocArgs = bodyArgs ?? ["Shopping"],
            HideContentForUserIds = hideFor ?? new HashSet<string>(StringComparer.Ordinal),
        };

    private static HouseholdPushRecipient Localized => new(Token, UserId, Localized: true);
    private static HouseholdPushRecipient Legacy => new(Token, UserId, Localized: false);

    // ══════════════════════════════════════════════════════════════════════════ The capability
    // gate ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ADeclaredBuild_GetsTheKeyOnBothPlatforms()
    {
        var message = HouseholdPushService.Build(Payload(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocKey,
                Is.EqualTo(HouseholdLocKeys.PantryLowListedBody));
            Assert.That(message.Android.Notification.BodyLocArgs, Is.EqualTo(new[] { "Shopping" }));

            // APNs names the body's key `loc-key`, with no `body-` prefix - the one place the two
            // platforms disagree on spelling.
            Assert.That(message.Apns.Aps.Alert.LocKey,
                Is.EqualTo(HouseholdLocKeys.PantryLowListedBody));
            Assert.That(message.Apns.Aps.Alert.LocArgs, Is.EqualTo(new[] { "Shopping" }));
        });
    }

    /// <summary>The whole reason the capability exists.</summary>
    [Test]
    public void AnUndeclaredBuild_GetsNoKeysAtAll()
    {
        var message = HouseholdPushService.Build(Payload(), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocKey, Is.Null);
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null);
            Assert.That(message.Apns.Aps.Alert.LocKey, Is.Null);
            Assert.That(message.Apns.Aps.Alert.LocArgs, Is.Null);
            Assert.That(message.Android.Notification.Body,
                Is.EqualTo("Running low, so it's gone on Shopping."));
        });
    }

    /// <summary>Sent alongside the key rather than instead of it: the capability says the bundle has
    /// the key, not that it has a translation for the reader's language, and English beats
    /// nothing.</summary>
    [Test]
    public void TheEnglishTravelsEvenWhenAKeyDoes()
    {
        var message = HouseholdPushService.Build(Payload(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.Title, Is.EqualTo("Milk"));
            Assert.That(message.Android.Notification.Body,
                Is.EqualTo("Running low, so it's gone on Shopping."));
            Assert.That(message.Apns.Aps.Alert.Body,
                Is.EqualTo("Running low, so it's gone on Shopping."));
        });
    }

    /// <summary>A pantry item's name, a shopping-list line, an expense description: the user typed
    /// it and it reads the same in every language.</summary>
    [Test]
    public void UserContent_CarriesNoTitleKey()
    {
        var message = HouseholdPushService.Build(Payload(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.TitleLocKey, Is.Null);
            Assert.That(message.Apns.Aps.Alert.TitleLocKey, Is.Null);
            Assert.That(message.Android.Notification.Title, Is.EqualTo("Milk"));
        });
    }

    /// <summary>Arguments without a key are the one combination the Firebase SDK rejects outright,
    /// which would throw inside the per-token loop and cost that recipient the notification.</summary>
    [Test]
    public void ArgumentsAreNeverSentWithoutTheirKey()
    {
        var message = HouseholdPushService.Build(
            Payload(bodyKey: null, bodyArgs: ["Shopping"]), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null);
            Assert.That(message.Apns.Aps.Alert.LocArgs, Is.Null);
        });
    }

    [Test]
    public void AKeyWithNoArguments_SendsAnEmptyListAsNull()
    {
        var message = HouseholdPushService.Build(
            Payload(bodyKey: HouseholdLocKeys.ChoreDueBody, bodyArgs: []), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocKey, Is.EqualTo(HouseholdLocKeys.ChoreDueBody));
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null,
                "an empty argument list is close enough to the rejected combination to never build");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Hidden content
    // (T2-23) ══════════════════════════════════════════════════════════════════════════

    /// <summary>Hiding the content is not a reason to switch the reader back into English, so the
    /// stand-in has keys of its own - and it must not carry the real key, which would let the OS
    /// render the very sentence the setting exists to withhold.</summary>
    [Test]
    public void HiddenContent_UsesTheStandInKeyAndNotTheRealOne()
    {
        var hide = new HashSet<string>(StringComparer.Ordinal) { UserId };
        var message = HouseholdPushService.Build(Payload(hideFor: hide), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.TitleLocKey, Is.EqualTo(HouseholdLocKeys.HiddenTitle));
            Assert.That(message.Android.Notification.BodyLocKey, Is.EqualTo(HouseholdLocKeys.HiddenBody));
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null,
                "the arguments are the content - 'Shopping' names a board this reader hid");
            Assert.That(message.Android.Notification.Body, Is.EqualTo(HouseholdPushService.HiddenContentBody));
            Assert.That(message.Android.Notification.Title, Is.EqualTo(HouseholdPushService.HiddenContentTitle));
        });
    }

    [Test]
    public void HiddenContent_OnAnUndeclaredBuild_StaysEnglishWithNoKeys()
    {
        var hide = new HashSet<string>(StringComparer.Ordinal) { UserId };
        var message = HouseholdPushService.Build(Payload(hideFor: hide), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.TitleLocKey, Is.Null);
            Assert.That(message.Android.Notification.BodyLocKey, Is.Null);
            Assert.That(message.Android.Notification.Body, Is.EqualTo(HouseholdPushService.HiddenContentBody));
        });
    }

    [Test]
    public void HiddenContent_AppliesPerRecipientAndNotPerNotification()
    {
        var hide = new HashSet<string>(StringComparer.Ordinal) { "ben" };

        var anna = HouseholdPushService.BuildData(Payload(hideFor: hide), Localized);
        var ben = HouseholdPushService.BuildData(
            Payload(hideFor: hide), new HouseholdPushRecipient("tok-2", "ben", Localized: true));

        Assert.Multiple(() =>
        {
            Assert.That(anna["hidden"], Is.EqualTo("0"));
            Assert.That(anna["title"], Is.EqualTo("Milk"));
            Assert.That(ben["hidden"], Is.EqualTo("1"));
            Assert.That(ben["title"], Is.EqualTo(HouseholdPushService.HiddenContentTitle));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The data payload the foreground client renders from
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A push arriving while the app is in front is drawn by neither platform, so the
    /// client draws it - and it needs the same key to produce the same sentence the OS would
    /// have.</summary>
    [Test]
    public void Data_MirrorsTheKeysForTheForegroundClient()
    {
        var data = HouseholdPushService.BuildData(Payload(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(data["type"], Is.EqualTo("household"));
            Assert.That(data["kind"], Is.EqualTo("pantry.low"));
            Assert.That(data["bodyLocKey"], Is.EqualTo(HouseholdLocKeys.PantryLowListedBody));
            Assert.That(data["bodyLocArgs"], Is.EqualTo("[\"Shopping\"]"),
                "FCM data values are strings only, so an argument list travels as JSON");
            Assert.That(data.ContainsKey("titleLocKey"), Is.False, "the title is user content");
        });
    }

    [Test]
    public void Data_OnAnUndeclaredBuild_CarriesNoKeys()
    {
        var data = HouseholdPushService.BuildData(Payload(), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(data.ContainsKey("bodyLocKey"), Is.False);
            Assert.That(data.ContainsKey("bodyLocArgs"), Is.False);
            Assert.That(data["body"], Is.EqualTo("Running low, so it's gone on Shopping."));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Delivery details that are invisible until they are wrong
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Without a channel id an OS-drawn notification lands on the app's fallback channel,
    /// so a house that silenced "Home" would still be buzzed by every chore - and the app's own
    /// foreground notifications would sit on a different channel from the OS's.</summary>
    [Test]
    public void Android_NamesTheHouseholdNotificationChannel()
    {
        var message = HouseholdPushService.Build(Payload(), Localized);

        Assert.That(message.Android.Notification.ChannelId,
            Is.EqualTo(HouseholdPushService.AndroidChannelId));
    }

    [Test]
    public void CollapsesOnTheRowRatherThanStacking()
    {
        var message = HouseholdPushService.Build(Payload(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.Tag, Is.EqualTo("pitm-1"));
            Assert.That(message.Apns.Headers["apns-collapse-id"], Is.EqualTo("pitm-1"));
        });
    }

    [Test]
    public void ATargetlessAlert_CollapsesOnItsKindInstead()
    {
        var payload = new HouseholdPushPayload
        {
            GuildId = "guild-1", Kind = "pantry.expiring", Title = "Use it or lose it",
        };

        var message = HouseholdPushService.Build(payload, Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Apns.Headers["apns-collapse-id"], Is.EqualTo("pantry.expiring"));
            Assert.That(message.Data.ContainsKey("targetId"), Is.False);
        });
    }

    [Test]
    public void ALongBodyIsTruncatedBeforeItLeaves()
    {
        var payload = new HouseholdPushPayload
        {
            GuildId = "guild-1", Kind = "decision.blocked", Title = "New sofa",
            Body = new string('x', 500),
        };

        var data = HouseholdPushService.BuildData(payload, Legacy);

        Assert.That(data["body"], Has.Length.EqualTo(300));
    }
}
