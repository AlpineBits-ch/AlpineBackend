using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>What a member should receive for one message in one channel.</summary>
public record ResolvedNotification
{
    public required NotificationLevel Level { get; init; }
    public required bool IsMuted { get; init; }
    public required bool SuppressEveryone { get; init; }
    public required bool SuppressRoleMentions { get; init; }
    public required bool MobilePush { get; init; }

    /// <summary>Whether a message with these mention characteristics should notify this member at
    /// all. Muting wins over everything - including a direct @mention - because a mute is an
    /// explicit "not now" and a member who wanted the exception would have set OnlyMentions
    /// instead.</summary>
    public bool ShouldNotify(bool isDirectMention, bool isRoleMention, bool isEveryoneMention)
    {
        if (IsMuted) return false;

        var mentionCounts =
            isDirectMention ||
            (isRoleMention && !SuppressRoleMentions) ||
            (isEveryoneMention && !SuppressEveryone);

        return Level switch
        {
            NotificationLevel.AllMessages => true,
            NotificationLevel.OnlyMentions => mentionCounts,
            _ => false,
        };
    }
}

/// <summary>
/// Resolves the channel → category → guild → default chain that decides whether a member hears
/// about a message. Lives beside GuildPermissionService and is shaped like it deliberately: same
/// per-member caching story, same "one choke point rather than every call site remembering the
/// precedence" reasoning.
/// </summary>
public class NotificationResolutionService(MicroserviceContext ctx)
{
    /// <summary>Discord's default for a guild you just joined, and ours. A member with no rows in
    /// either table resolves to exactly this.</summary>
    public static readonly ResolvedNotification Default = new()
    {
        Level = NotificationLevel.AllMessages,
        IsMuted = false,
        SuppressEveryone = false,
        SuppressRoleMentions = false,
        MobilePush = true,
    };

    /// <summary>
    /// Resolves for many members at once. Batch-shaped rather than per-member because the caller
    /// is the message-created path deciding who to notify - resolving a 500-member guild one
    /// member at a time would be 1000 queries per message.
    /// </summary>
    public async Task<Dictionary<string, ResolvedNotification>> ResolveForChannelAsync(
        string channelId, IReadOnlyCollection<string> memberIds)
    {
        var resolved = memberIds.ToDictionary(id => id, _ => Default);
        if (memberIds.Count == 0) return resolved;

        // The channel's category, needed to find category-level overrides. One lookup for the
        // whole batch since every member is being resolved against the same channel.
        var categoryId = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.CategoryId)
            .FirstOrDefaultAsync();

        var guildSettings = await ctx.GuildNotificationSettings
            .AsNoTracking()
            .Where(s => memberIds.Contains(s.MemberId))
            .ToDictionaryAsync(s => s.MemberId);

        var overrides = await ctx.NotificationOverrides
            .AsNoTracking()
            .Where(o => memberIds.Contains(o.MemberId)
                        && (o.ChannelId == channelId || (categoryId != null && o.CategoryId == categoryId)))
            .ToListAsync();

        var channelOverrides = overrides
            .Where(o => o.ChannelId == channelId)
            .ToDictionary(o => o.MemberId);

        var categoryOverrides = overrides
            .Where(o => o.CategoryId != null)
            .ToDictionary(o => o.MemberId);

        var now = DateTimeOffset.UtcNow;

        foreach (var memberId in memberIds)
        {
            guildSettings.TryGetValue(memberId, out var guildSetting);
            channelOverrides.TryGetValue(memberId, out var channelOverride);
            categoryOverrides.TryGetValue(memberId, out var categoryOverride);

            resolved[memberId] = Resolve(guildSetting, categoryOverride, channelOverride, now);
        }

        return resolved;
    }

    /// <summary>Single-member convenience for the settings endpoints, which show a member what
    /// their own effective level is.</summary>
    public async Task<ResolvedNotification> ResolveForChannelAsync(string channelId, string memberId)
    {
        var many = await ResolveForChannelAsync(channelId, [memberId]);
        return many[memberId];
    }

    /// <summary>
    /// The precedence itself, pure and separately testable.
    ///
    /// Level and mute resolve *independently*: the most specific row that expresses a level wins
    /// the level, and the most specific row that expresses a mute wins the mute. They are not
    /// taken as a unit, because a channel override that only mutes ("silence this one channel for
    /// an hour") must not also reset the level it inherited from the guild.
    /// </summary>
    public static ResolvedNotification Resolve(
        GuildNotificationSetting? guildSetting,
        NotificationOverride? categoryOverride,
        NotificationOverride? channelOverride,
        DateTimeOffset now)
    {
        var level =
            channelOverride?.Level
            ?? categoryOverride?.Level
            ?? guildSetting?.Level
            ?? Default.Level;

        var muted =
            (channelOverride?.MutedUntil is not null && channelOverride.IsMuted(now))
            || (channelOverride?.MutedUntil is null && categoryOverride?.MutedUntil is not null && categoryOverride.IsMuted(now))
            || (channelOverride?.MutedUntil is null && categoryOverride?.MutedUntil is null && guildSetting?.IsMuted(now) == true);

        return new ResolvedNotification
        {
            Level = level,
            IsMuted = muted,
            // The suppression flags and the push switch are guild-wide by design - per-channel
            // "mute @everyone here but not there" is a knob nobody asks for, and each one added
            // here multiplies the override table's meaning.
            SuppressEveryone = guildSetting?.SuppressEveryone ?? Default.SuppressEveryone,
            SuppressRoleMentions = guildSetting?.SuppressRoleMentions ?? Default.SuppressRoleMentions,
            MobilePush = guildSetting?.MobilePush ?? Default.MobilePush,
        };
    }
}
