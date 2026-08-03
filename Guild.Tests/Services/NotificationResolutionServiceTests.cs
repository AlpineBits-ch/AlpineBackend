using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Services;

/// <summary>
/// Covers NotificationResolutionService.Resolve - the pure precedence function - directly, since
/// that is where a bug would actually live.
/// </summary>
[TestFixture]
public class NotificationResolutionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = Now.AddHours(1);
    private static readonly DateTimeOffset Past = Now.AddHours(-1);

    private static GuildNotificationSetting GuildSetting(
        NotificationLevel level = NotificationLevel.AllMessages,
        DateTimeOffset? mutedUntil = null,
        bool suppressEveryone = false,
        bool suppressRoleMentions = false,
        bool mobilePush = true) => new()
    {
        Id = "gnot-1", MemberId = "member-1", Level = level, MutedUntil = mutedUntil,
        SuppressEveryone = suppressEveryone, SuppressRoleMentions = suppressRoleMentions,
        MobilePush = mobilePush,
    };

    private static NotificationOverride Override(NotificationLevel? level = null, DateTimeOffset? mutedUntil = null) => new()
    {
        Id = "nover-1", MemberId = "member-1", Level = level, MutedUntil = mutedUntil,
    };

    // ══════════════════════════════════════════════════════════════════════ Precedence
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void Resolve_NothingConfigured_FallsBackToDefault()
    {
        var result = NotificationResolutionService.Resolve(null, null, null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Level, Is.EqualTo(NotificationLevel.AllMessages));
            Assert.That(result.IsMuted, Is.False);
            Assert.That(result.MobilePush, Is.True);
        });
    }

    [Test]
    public void Resolve_GuildSettingOnly_IsUsed()
    {
        var result = NotificationResolutionService.Resolve(GuildSetting(NotificationLevel.OnlyMentions), null, null, Now);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    [Test]
    public void Resolve_CategoryOverride_BeatsGuild()
    {
        var result = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.AllMessages),
            Override(NotificationLevel.Nothing),
            null, Now);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.Nothing));
    }

    [Test]
    public void Resolve_ChannelOverride_BeatsCategoryAndGuild()
    {
        var result = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.Nothing),
            Override(NotificationLevel.Nothing),
            Override(NotificationLevel.AllMessages),
            Now);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.AllMessages),
            "the most specific level wins even when it is louder than what it overrides");
    }

    [Test]
    public void Resolve_ChannelOverrideWithNullLevel_InheritsRatherThanResetting()
    {
        // A mute-only channel override: the member silenced one channel for an hour but did not
        // change what level it is on.
        var result = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.OnlyMentions),
            null,
            Override(level: null, mutedUntil: Future),
            Now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Level, Is.EqualTo(NotificationLevel.OnlyMentions));
            Assert.That(result.IsMuted, Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Guild default
    // (Discord's default_message_notifications)

    [Test]
    public void Resolve_NoMemberPreferences_UsesTheGuildDefault()
    {
        var result = NotificationResolutionService.Resolve(
            null, null, null, Now, NotificationLevel.OnlyMentions);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    [Test]
    public void Resolve_MemberGuildSetting_BeatsTheGuildDefault()
    {
        var result = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.AllMessages), null, null, Now, NotificationLevel.OnlyMentions);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.AllMessages),
            "an explicit member preference is the whole point of having one - a quiet guild default must not override someone who asked for everything");
    }

    [Test]
    public void Resolve_ChannelOverride_BeatsTheGuildDefault()
    {
        var result = NotificationResolutionService.Resolve(
            null, null, Override(NotificationLevel.AllMessages), Now, NotificationLevel.Nothing);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.AllMessages));
    }

    /// <summary>Omitting the parameter has to keep meaning AllMessages: every pre-existing caller
    /// relies on it, and a silent change of default would flip notification behaviour for every
    /// guild that has never set one.</summary>
    [Test]
    public void Resolve_GuildDefaultOmitted_StillFallsBackToAllMessages()
    {
        var result = NotificationResolutionService.Resolve(null, null, null, Now);

        Assert.That(result.Level, Is.EqualTo(NotificationLevel.AllMessages));
    }

    [Test]
    public void Resolve_GuildDefaultNothing_SilencesAMemberWhoSetNothing()
    {
        var result = NotificationResolutionService.Resolve(
            null, null, null, Now, NotificationLevel.Nothing);

        Assert.Multiple(() =>
        {
            Assert.That(result.Level, Is.EqualTo(NotificationLevel.Nothing));
            Assert.That(result.ShouldNotify(isDirectMention: true, isRoleMention: false, isEveryoneMention: false), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Mute resolution, which is independent of level resolution
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void Resolve_ExpiredMute_IsNotMuted()
    {
        var result = NotificationResolutionService.Resolve(GuildSetting(mutedUntil: Past), null, null, Now);

        Assert.That(result.IsMuted, Is.False);
    }

    [Test]
    public void Resolve_ChannelMuteOverridesGuildMute_WhenChannelUnmutesExplicitly()
    {
        // Guild muted, but this one channel has its own already-expired mute - the channel row
        // expresses a mute state, so it is the one that counts and the member hears this channel.
        var result = NotificationResolutionService.Resolve(
            GuildSetting(mutedUntil: Future),
            null,
            Override(mutedUntil: Past),
            Now);

        Assert.That(result.IsMuted, Is.False,
            "the most specific row that expresses a mute decides, even when it means 'not muted'");
    }

    [Test]
    public void Resolve_GuildMuteAppliesWhenNoOverrideMentionsMuting()
    {
        var result = NotificationResolutionService.Resolve(
            GuildSetting(mutedUntil: Future),
            null,
            Override(level: NotificationLevel.AllMessages),
            Now);

        Assert.That(result.IsMuted, Is.True,
            "a level-only channel override must not accidentally clear an inherited mute");
    }

    [Test]
    public void Resolve_CategoryMuteAppliesWhenChannelIsSilentOnMuting()
    {
        var result = NotificationResolutionService.Resolve(
            GuildSetting(),
            Override(mutedUntil: Future),
            Override(level: NotificationLevel.AllMessages),
            Now);

        Assert.That(result.IsMuted, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ ShouldNotify
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void ShouldNotify_AllMessages_NotifiesOnAPlainMessage()
    {
        var resolved = NotificationResolutionService.Resolve(GuildSetting(NotificationLevel.AllMessages), null, null, Now);

        Assert.That(resolved.ShouldNotify(false, false, false), Is.True);
    }

    [Test]
    public void ShouldNotify_OnlyMentions_IgnoresPlainMessages()
    {
        var resolved = NotificationResolutionService.Resolve(GuildSetting(NotificationLevel.OnlyMentions), null, null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.ShouldNotify(false, false, false), Is.False);
            Assert.That(resolved.ShouldNotify(true, false, false), Is.True);
        });
    }

    [Test]
    public void ShouldNotify_Nothing_NeverNotifies_EvenOnADirectMention()
    {
        var resolved = NotificationResolutionService.Resolve(GuildSetting(NotificationLevel.Nothing), null, null, Now);

        Assert.That(resolved.ShouldNotify(true, true, true), Is.False);
    }

    [Test]
    public void ShouldNotify_Muted_BeatsEvenADirectMention()
    {
        var resolved = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.AllMessages, mutedUntil: Future), null, null, Now);

        Assert.That(resolved.ShouldNotify(true, true, true), Is.False,
            "a mute is an explicit 'not now'; someone wanting the mention exception sets OnlyMentions");
    }

    [Test]
    public void ShouldNotify_SuppressEveryone_DropsEveryonePingsButKeepsDirectOnes()
    {
        var resolved = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.OnlyMentions, suppressEveryone: true), null, null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.ShouldNotify(false, false, true), Is.False, "@everyone is suppressed");
            Assert.That(resolved.ShouldNotify(true, false, true), Is.True, "a direct mention still lands");
        });
    }

    [Test]
    public void ShouldNotify_SuppressRoleMentions_DropsRolePingsOnly()
    {
        var resolved = NotificationResolutionService.Resolve(
            GuildSetting(NotificationLevel.OnlyMentions, suppressRoleMentions: true), null, null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.ShouldNotify(false, true, false), Is.False);
            Assert.That(resolved.ShouldNotify(true, true, false), Is.True);
        });
    }

    [Test]
    public void Resolve_SuppressionAndPushFlagsComeFromTheGuildRowOnly()
    {
        // Overrides carry no suppression flags by design; a channel override must not be able to
        // silently re-enable @everyone pings the member turned off guild-wide.
        var result = NotificationResolutionService.Resolve(
            GuildSetting(suppressEveryone: true, mobilePush: false),
            Override(NotificationLevel.AllMessages),
            Override(NotificationLevel.AllMessages),
            Now);

        Assert.Multiple(() =>
        {
            Assert.That(result.SuppressEveryone, Is.True);
            Assert.That(result.MobilePush, Is.False);
        });
    }
}
