using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// A member's per-channel or per-category deviation from their guild-level
/// <see cref="GuildNotificationSetting"/>.
///
/// Channel and category overrides share one table with a nullable id each (exactly one is set)
/// rather than living in two near-identical tables. They have identical shape, identical
/// lifecycle and are read together on every resolution - two tables would mean two queries and
/// two sets of migrations to express one concept. This mirrors how ChannelPermission already
/// carries both a ChannelId and a CategoryId for the same reason.
/// </summary>
public class NotificationOverride : BaseEntity<NotificationOverride>, IPrefixedEntity
{
    public static string Prefix { get; } = "nover";

    public string MemberId { get; set; } = null!;
    public virtual GuildMember GuildMember { get; set; } = null!;

    /// <summary>Set for a channel override; null for a category one.</summary>
    public string? ChannelId { get; set; }
    public virtual Aggregates.Channel? Channel { get; set; }

    /// <summary>Set for a category override; null for a channel one.</summary>
    public string? CategoryId { get; set; }
    public virtual Category? Category { get; set; }

    /// <summary>Null means "inherit whatever the next level up resolves to" - which is different
    /// from any concrete value, and is why this is nullable rather than defaulting to AllMessages.
    /// A row with a null Level and a set MutedUntil is a pure mute that keeps the inherited level
    /// for when the mute expires.</summary>
    public NotificationLevel? Level { get; set; }

    public DateTimeOffset? MutedUntil { get; set; }

    public bool IsMuted(DateTimeOffset now) => MutedUntil is not null && MutedUntil > now;

    /// <summary>True once the row carries no preference at all, at which point the caller deletes
    /// it rather than storing a row that means "inherit everything".</summary>
    public bool IsEmpty() => Level is null && MutedUntil is null;

    public static NotificationOverride ForChannel(string memberId, string channelId)
    {
        var date = DateTimeOffset.UtcNow;
        return new NotificationOverride
        {
            Id = GenerateId(), CreatedAt = date, UpdatedAt = date,
            MemberId = memberId, ChannelId = channelId,
        };
    }

    public static NotificationOverride ForCategory(string memberId, string categoryId)
    {
        var date = DateTimeOffset.UtcNow;
        return new NotificationOverride
        {
            Id = GenerateId(), CreatedAt = date, UpdatedAt = date,
            MemberId = memberId, CategoryId = categoryId,
        };
    }
}
