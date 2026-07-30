using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// One member's notification preferences for one guild - the base of the resolution chain that
/// <c>NotificationResolutionService</c> walks (channel override → category override → this → the
/// AllMessages default).
/// </summary>
public class GuildNotificationSetting : BaseEntity<GuildNotificationSetting>, IPrefixedEntity
{
    public static string Prefix { get; } = "gnot";

    public string MemberId { get; set; } = null!;
    public virtual GuildMember GuildMember { get; set; } = null!;

    public NotificationLevel Level { get; set; } = NotificationLevel.AllMessages;

    /// <summary>Null when not muted.</summary>
    public DateTimeOffset? MutedUntil { get; set; }

    /// <summary>Drop @everyone/@here for this guild even at OnlyMentions.</summary>
    public bool SuppressEveryone { get; set; }

    /// <summary>Same, for @role pings.</summary>
    public bool SuppressRoleMentions { get; set; }

    /// <summary>When false, this guild never produces a push notification even if Level would
    /// otherwise notify - the in-app badge still updates. Lets someone follow a guild actively at
    /// their desk without it reaching their phone.</summary>
    public bool MobilePush { get; set; } = true;

    public bool IsMuted(DateTimeOffset now) => MutedUntil is not null && MutedUntil > now;

    public static GuildNotificationSetting CreateDefault(string memberId)
    {
        var date = DateTimeOffset.UtcNow;
        return new GuildNotificationSetting
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            MemberId = memberId,
            Level = NotificationLevel.AllMessages,
        };
    }
}
