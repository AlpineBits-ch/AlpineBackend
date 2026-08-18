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

    /// <summary>
    /// Whether a message with these mention characteristics should notify this member at all.
    /// </summary>
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
/// about a message.
/// </summary>
public class NotificationResolutionService(MicroserviceContext ctx)
{
    /// <summary>Discord's default for a guild you just joined, and ours.</summary>
    public static readonly ResolvedNotification Default = new()
    {
        Level = NotificationLevel.AllMessages,
        IsMuted = false,
        SuppressEveryone = false,
        SuppressRoleMentions = false,
        MobilePush = true,
    };

    /// <summary>Resolves for many members at once.</summary>
    public async Task<Dictionary<string, ResolvedNotification>> ResolveForChannelAsync(
        string channelId, IReadOnlyCollection<string> memberIds)
    {
        var resolved = memberIds.ToDictionary(id => id, _ => Default);
        if (memberIds.Count == 0) return resolved;

        // The channel's category (needed to find category-level overrides) and its guild's default
        // level (the bottom of the chain).
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
    /// Resolves one member against many channels - the inverse of <see
    /// cref="ResolveForChannelAsync(string, IReadOnlyCollection{string})"/>, which resolves many
    /// members against one channel.
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
    /// resolves to AllMessages.
    /// </summary>
    /// <param name="mentionedMemberIds">Members the message named.</param>
    /// <param name="authorUserId">Excluded: nobody is notified about their own message.</param>
    /// <param name="includeEveryMember">
    /// Set for an @everyone, whose candidate set is the membership by definition.
    /// </param>
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

    /// <summary>
    /// Which of these users may be sent a phone notification about something in this channel:
    /// whoever has not muted it and still has mobile push on.
    /// </summary>
    /// <param name="guildId">The guild the notification is about.</param>
    /// <param name="channelId">The channel it came from, or null for a guild-scoped one.</param>
    /// <param name="userIds">The candidate recipients.</param>
    /// <returns>The subset that may be pushed to.</returns>
    public async Task<List<string>> PushableUserIdsAsync(
        string guildId, string? channelId, IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) return [];

        var members = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && userIds.Contains(m.UserId))
            .Select(m => new { m.Id, m.UserId })
            .ToListAsync();

        if (members.Count == 0) return [];

        // Without a channel there is nothing to resolve overrides against, so the guild-level
        // settings are the whole chain - which is what ResolveForChannelAsync degrades to when the
        // channel lookup misses.
        var resolved = await ResolveForChannelAsync(channelId ?? "", members.Select(m => m.Id).ToList());

        return members
            .Where(m => resolved.TryGetValue(m.Id, out var setting) && !setting.IsMuted && setting.MobilePush)
            .Select(m => m.UserId)
            .ToList();
    }

    /// <summary>Single-member convenience for the settings endpoints, which show a member what
    /// their own effective level is.</summary>
    public async Task<ResolvedNotification> ResolveForChannelAsync(string channelId, string memberId)
    {
        var many = await ResolveForChannelAsync(channelId, [memberId]);
        return many[memberId];
    }

    /// <summary>The precedence itself, pure and separately testable.</summary>
    /// <param name="guildDefault">
    /// The guild's DefaultMessageNotifications - what a member with no preferences of their own
    /// falls back to.
    /// </param>
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
