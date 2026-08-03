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

        // The channel's category (needed to find category-level overrides) and its guild's default
        // level (the bottom of the chain). One lookup for the whole batch, since every member is
        // being resolved against the same channel.
        var scope = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.CategoryId, GuildDefault = c.Guild.DefaultMessageNotifications })
            .FirstOrDefaultAsync();

        var categoryId = scope?.CategoryId;
        var guildDefault = scope?.GuildDefault ?? Default.Level;

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

            resolved[memberId] = Resolve(guildSetting, categoryOverride, channelOverride, now, guildDefault);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves one member against many channels - the inverse of
    /// <see cref="ResolveForChannelAsync(string, IReadOnlyCollection{string})"/>, which resolves many
    /// members against one channel.
    ///
    /// The inbox needs this axis: it holds one caller's read states spanning every guild they are
    /// in, and has to know which of those channels are muted. Reusing the per-channel method would
    /// mean one call per channel.
    ///
    /// Shares the same pure <see cref="Resolve"/>, so the precedence rules cannot drift between the
    /// two axes.
    /// </summary>
    public async Task<Dictionary<string, ResolvedNotification>> ResolveForMemberAsync(
        string memberId, IReadOnlyCollection<string> channelIds)
    {
        var resolved = await ResolveForMemberChannelsAsync(
            channelIds.Select(c => (memberId, c)).ToList());

        return channelIds.ToDictionary(
            id => id,
            id => resolved.GetValueOrDefault((memberId, id), Default),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves many (member, channel) pairs at once, in three queries whatever the shape.
    ///
    /// The inbox needs this because one page spans several guilds, and the caller holds a different
    /// GuildMember row in each - so neither "one member, many channels" nor "many members, one
    /// channel" covers it, and looping either of those means a query per guild per page.
    /// </summary>
    public async Task<Dictionary<(string MemberId, string ChannelId), ResolvedNotification>>
        ResolveForMemberChannelsAsync(IReadOnlyCollection<(string MemberId, string ChannelId)> pairs)
    {
        var resolved = new Dictionary<(string, string), ResolvedNotification>();
        if (pairs.Count == 0) return resolved;

        var memberIds = pairs.Select(p => p.MemberId).Distinct().ToList();
        var channelIds = pairs.Select(p => p.ChannelId).Distinct().ToList();

        // Each channel's category and its guild's default, in one query for the whole page.
        var scopes = await ctx.Channels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CategoryId, GuildDefault = c.Guild.DefaultMessageNotifications })
            .ToListAsync();

        var scopeById = scopes.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var guildSettings = await ctx.GuildNotificationSettings
            .AsNoTracking()
            .Where(s => memberIds.Contains(s.MemberId))
            .ToDictionaryAsync(s => s.MemberId);

        var categoryIds = scopes
            .Where(s => s.CategoryId != null)
            .Select(s => s.CategoryId!)
            .Distinct()
            .ToList();

        var overrides = await ctx.NotificationOverrides
            .AsNoTracking()
            .Where(o => memberIds.Contains(o.MemberId)
                        && ((o.ChannelId != null && channelIds.Contains(o.ChannelId))
                            || (o.CategoryId != null && categoryIds.Contains(o.CategoryId))))
            .ToListAsync();

        var byChannel = overrides
            .Where(o => o.ChannelId != null)
            .ToDictionary(o => (o.MemberId, o.ChannelId!));

        var byCategory = overrides
            .Where(o => o.CategoryId != null && o.ChannelId == null)
            .ToDictionary(o => (o.MemberId, o.CategoryId!));

        var now = DateTimeOffset.UtcNow;

        foreach (var (memberId, channelId) in pairs)
        {
            if (!scopeById.TryGetValue(channelId, out var scope))
            {
                resolved[(memberId, channelId)] = Default;
                continue;
            }

            guildSettings.TryGetValue(memberId, out var guildSetting);
            byChannel.TryGetValue((memberId, channelId), out var channelOverride);

            NotificationOverride? categoryOverride = null;
            if (scope.CategoryId is not null) byCategory.TryGetValue((memberId, scope.CategoryId), out categoryOverride);

            resolved[(memberId, channelId)] =
                Resolve(guildSetting, categoryOverride, channelOverride, now, scope.GuildDefault);
        }

        return resolved;
    }

    /// <summary>A member who might need telling about a message, with the user id the push is
    /// addressed to.</summary>
    public readonly record struct NotifiableCandidate(string MemberId, string UserId);

    /// <summary>
    /// The members who could possibly be notified about one message: whoever it named, plus whoever
    /// resolves to AllMessages. Deliberately a superset - <see cref="Resolve"/> and
    /// <see cref="ResolvedNotification.ShouldNotify"/> still decide, this only avoids asking them
    /// about members whose answer cannot be yes.
    ///
    /// The point is the else-branch. At the AllMessages guild default every member is a candidate,
    /// because that is literally what the setting asks for and no query can shrink it. Under any
    /// quieter default the set collapses to the mentioned members plus the few who explicitly opted
    /// back in, which is what stops an ordinary message in a large guild costing a full membership
    /// scan.
    /// </summary>
    /// <param name="mentionedMemberIds">Members the message named. Always candidates, whatever
    /// their level - OnlyMentions exists for exactly this case.</param>
    /// <param name="authorUserId">Excluded: nobody is notified about their own message.</param>
    /// <param name="includeEveryMember">Set for an @everyone, whose candidate set is the membership
    /// by definition. That cost is what the ping means; it is paid on the push path only, and never
    /// by the mention index.</param>
    public async Task<List<NotifiableCandidate>> NotifiableCandidatesAsync(
        string guildId, string channelId, IReadOnlyCollection<string> mentionedMemberIds, string authorUserId,
        bool includeEveryMember = false)
    {
        var scope = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.CategoryId, GuildDefault = c.Guild.DefaultMessageNotifications })
            .FirstOrDefaultAsync();

        var guildDefault = scope?.GuildDefault ?? Default.Level;

        if (includeEveryMember || guildDefault == NotificationLevel.AllMessages)
        {
            return await ctx.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == guildId && m.UserId != authorUserId)
                .Select(m => new NotifiableCandidate(m.Id, m.UserId))
                .ToListAsync();
        }

        var candidateIds = new HashSet<string>(mentionedMemberIds, StringComparer.Ordinal);

        // Opted back in at the guild level...
        candidateIds.UnionWith(await ctx.GuildNotificationSettings
            .AsNoTracking()
            .Where(s => s.GuildMember.GuildId == guildId && s.Level == NotificationLevel.AllMessages)
            .Select(s => s.MemberId)
            .ToListAsync());

        // ...or for this channel or its category specifically.
        candidateIds.UnionWith(await ctx.NotificationOverrides
            .AsNoTracking()
            .Where(o => o.Level == NotificationLevel.AllMessages
                        && (o.ChannelId == channelId
                            || (scope!.CategoryId != null && o.CategoryId == scope.CategoryId)))
            .Select(o => o.MemberId)
            .ToListAsync());

        if (candidateIds.Count == 0) return [];

        return await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && candidateIds.Contains(m.Id) && m.UserId != authorUserId)
            .Select(m => new NotifiableCandidate(m.Id, m.UserId))
            .ToListAsync();
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
    /// <param name="guildDefault">The guild's DefaultMessageNotifications - what a member with no
    /// preferences of their own falls back to. Optional so the many callers that only care about
    /// precedence keep working; omitting it means the historical AllMessages default.</param>
    public static ResolvedNotification Resolve(
        GuildNotificationSetting? guildSetting,
        NotificationOverride? categoryOverride,
        NotificationOverride? channelOverride,
        DateTimeOffset now,
        NotificationLevel? guildDefault = null)
    {
        var level =
            channelOverride?.Level
            ?? categoryOverride?.Level
            ?? guildSetting?.Level
            ?? guildDefault
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
