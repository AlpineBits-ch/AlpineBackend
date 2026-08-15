using Guild.Contracts;
using Messaging.Application.Services;

namespace Messaging.Tests.Services;

/// <summary>The shape of a voice-ring push on the wire.</summary>
[TestFixture]
public class VoiceRingPushServiceTests
{
    private const string Token = "tok-1";
    private const string UserId = "user-target";

    private static VoiceRingPushPayload Invite(bool hidden = false) => new()
    {
        RingId = "ring_1",
        GuildId = "guild-1",
        ChannelId = "chan-1",
        InviterId = "user-inviter",
        InviterAvatarUrl = "https://cdn/a.png",
        ExpiresInSeconds = 60,
        Title = "Ada",
        Body = "Asked you to join General.",
        BodyLocKey = VoiceLocKeys.InviteBody,
        BodyLocArgs = ["General"],
        Hidden = hidden,
    };

    private static VoiceRingPushPayload Cancel() => new()
    {
        RingId = "ring_1",
        GuildId = "guild-1",
        ChannelId = "chan-1",
        InviterId = "user-inviter",
        Cancel = true,
        CancelReason = "Accepted",
        ExcludeDeviceId = "device-phone",
    };

    private static VoiceRingPushRecipient Localized => new(Token, UserId, Localized: true);
    private static VoiceRingPushRecipient Legacy => new(Token, UserId, Localized: false);

    // ══════════════════════════════════════════════════════════════════════════ The capability
    // gate ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ADeclaredBuildGetsTheKeyOnBothPlatforms()
    {
        var message = VoiceRingPushService.Build(Invite(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocKey, Is.EqualTo(VoiceLocKeys.InviteBody));
            Assert.That(message.Android.Notification.BodyLocArgs, Is.EqualTo(new[] { "General" }));

            // APNs names the body's key `loc-key`, with no `body-` prefix - the one place the two
            // platforms disagree on spelling.
            Assert.That(message.Apns.Aps.Alert.LocKey, Is.EqualTo(VoiceLocKeys.InviteBody));
            Assert.That(message.Apns.Aps.Alert.LocArgs, Is.EqualTo(new[] { "General" }));
        });
    }

    /// <summary>The whole reason the capability exists.</summary>
    [Test]
    public void AnUndeclaredBuildGetsNoKeysAtAll()
    {
        var message = VoiceRingPushService.Build(Invite(), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.BodyLocKey, Is.Null);
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null,
                "the Firebase SDK rejects arguments with no key to put them in");
            Assert.That(message.Apns.Aps.Alert.LocKey, Is.Null);
            Assert.That(message.Android.Notification.Body, Is.EqualTo("Asked you to join General."));
        });
    }

    [Test]
    public void TheEnglishTravelsAlongsideTheKey()
    {
        var message = VoiceRingPushService.Build(Invite(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.Title, Is.EqualTo("Ada"));
            Assert.That(message.Android.Notification.Body, Is.EqualTo("Asked you to join General."),
                "this is what shows on a handset in a language the bundle has no translation for");
        });
    }

    [Test]
    public void TheInvitersOwnNameNeverCarriesAKey()
    {
        var message = VoiceRingPushService.Build(Invite(), Localized);

        Assert.That(message.Android.Notification.TitleLocKey, Is.Null,
            "it is what somebody called themselves, and it reads the same in every language");
    }

    // ══════════════════════════════════════════════════════════════════════════ Hidden content
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void HiddenContentWithholdsThePersonTheChannelAndTheAvatar()
    {
        var message = VoiceRingPushService.Build(Invite(hidden: true), Localized);
        var data = VoiceRingPushService.BuildData(Invite(hidden: true), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.Title, Is.EqualTo(VoiceRingPushService.HiddenContentTitle));
            Assert.That(message.Android.Notification.Body, Is.EqualTo(VoiceRingPushService.HiddenContentBody));
            Assert.That(message.Android.Notification.BodyLocKey, Is.EqualTo(VoiceLocKeys.HiddenBody));
            Assert.That(message.Android.Notification.BodyLocArgs, Is.Null,
                "the hidden copy takes no arguments, and the channel name is exactly what it is hiding");
            Assert.That(data.ContainsKey("inviterAvatarUrl"), Is.False);
            Assert.That(data["hidden"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void HiddenWinsOverLocalizedForABuildThatCannotResolveKeys()
    {
        var message = VoiceRingPushService.Build(Invite(hidden: true), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification.Body, Is.EqualTo(VoiceRingPushService.HiddenContentBody));
            Assert.That(message.Android.Notification.BodyLocKey, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Routing data
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void TheDataPayloadIdentifiesItselfOnTheFirstKeyAClientReads()
    {
        var data = VoiceRingPushService.BuildData(Invite(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(data["type"], Is.EqualTo("voice_ring"));
            Assert.That(data["ringSubtype"], Is.EqualTo("invite"));
            Assert.That(data["ringId"], Is.EqualTo("ring_1"));
            Assert.That(data["channelId"], Is.EqualTo("chan-1"));
            Assert.That(data["inviterId"], Is.EqualTo("user-inviter"));
            Assert.That(data["recipientUserId"], Is.EqualTo(UserId));
            Assert.That(data["expiresInSeconds"], Is.EqualTo("60"),
                "a push that sat in a queue longer than this is describing an invitation that is already dead");
        });
    }

    [Test]
    public void TheArgumentListTravelsAsJsonBecauseFcmDataValuesAreStrings()
    {
        var data = VoiceRingPushService.BuildData(Invite(), Localized);

        Assert.That(data["bodyLocArgs"], Is.EqualTo("[\"General\"]"));
    }

    [Test]
    public void AnUndeclaredBuildGetsNoKeysInTheDataEither()
    {
        var data = VoiceRingPushService.BuildData(Invite(), Legacy);

        Assert.Multiple(() =>
        {
            Assert.That(data.ContainsKey("bodyLocKey"), Is.False);
            Assert.That(data.ContainsKey("bodyLocArgs"), Is.False);
            Assert.That(data["body"], Is.EqualTo("Asked you to join General."));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The cancel
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void TheCancelDrawsNothing()
    {
        var message = VoiceRingPushService.Build(Cancel(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(message.Android.Notification, Is.Null,
                "buzzing somebody again to say an invitation expired is worse than leaving it up");
            Assert.That(message.Apns.Aps.Alert, Is.Null);
            Assert.That(message.Apns.Aps.ContentAvailable, Is.True);
            Assert.That(message.Apns.Headers["apns-push-type"], Is.EqualTo("background"));
        });
    }

    [Test]
    public void TheCancelNamesTheDeviceThatAnsweredSoThatDeviceCanIgnoreIt()
    {
        var data = VoiceRingPushService.BuildData(Cancel(), Localized);

        Assert.Multiple(() =>
        {
            Assert.That(data["ringSubtype"], Is.EqualTo("cancel"));
            Assert.That(data["cancelReason"], Is.EqualTo("Accepted"));
            Assert.That(data["excludeDeviceId"], Is.EqualTo("device-phone"));
        });
    }

    [Test]
    public void TheCancelCollapsesOntoTheSameNotificationAsTheInvite()
    {
        var invite = VoiceRingPushService.Build(Invite(), Localized);
        var cancel = VoiceRingPushService.Build(Cancel(), Localized);

        Assert.That(cancel.Apns.Headers["apns-collapse-id"],
            Is.EqualTo(invite.Apns.Headers["apns-collapse-id"]),
            "cancelling under a different key would leave the card it was meant to replace on screen");
    }

    [Test]
    public void TheInviteLandsOnItsOwnAndroidChannel()
    {
        var message = VoiceRingPushService.Build(Invite(), Localized);

        Assert.That(message.Android.Notification.ChannelId,
            Is.EqualTo(VoiceRingPushService.AndroidChannelId),
            "somebody who does not want to be pulled into calls must be able to silence exactly that");
    }
}
