using System.Text;
using Guild.Application.Dtos.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>
/// The Unread tab: every channel, across every guild the caller is in, holding something newer than
/// their read cursor.
/// </summary>
public class InboxService(
    MicroserviceContext ctx,
    NotificationResolutionService notifications,
    GuildPermissionService permissions,
    IMessageBus bus,
    ILogger<InboxService> logger)
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 25;

    /// <summary>How many messages are shown under one group.</summary>
    public const int MaxPreviewMessages = 5;

    /// <summary>Ceiling on the summary badge.</summary>
    public const int MaxSummaryCount = 99;

    /// <summary>The guild's icon route, as the client should call it.</summary>
    public static string GuildIconUrl(string guildId) => $"/api/v1/guild/guilds/{guildId}/icon";

    public static string GuildIconThumbnailUrl(string guildId) => $"{GuildIconUrl(guildId)}/thumbnail";

    /// <summary>One row of the unread query, before muting and permissions have had their say.
    /// Internal rather than private so the translation tests can assert the query this projects
    /// into actually compiles to SQL - see InboxQueryTranslationTests.</summary>
    internal sealed record UnreadRow(
        string MemberId,
        string GuildId,
        string GuildName,
        string ChannelId,
        string ChannelName,
        ChannelType ChannelType,
        string? CategoryId,
        string? CategoryName,
        string? ParentChannelId,
        string? ParentChannelName,
        DateTimeOffset LastActivityAt,
        string? LastMessageId,
        int MessageCount,
        string? LastReadMessageId,
        int MessageCountAtRead,
        DateTimeOffset? LastReadAt,
        DateTimeOffset JoinedAt);

    public async Task<InboxUnreadPageDto> GetUnreadAsync(string userId, int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        var rows = await QueryUnreadAsync(userId, limit + 1, cursor);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;

        page = await DropMutedAsync(page);
        page = await DropUnviewableAsync(userId, page);

        var (previews, previewsUnavailable) = await LoadPreviewsAsync(page);
        var mentionCounts = await CountMentionsAsync(userId, page);

        var groups = page.Select(row =>
        {
            var messages = previews.GetValueOrDefault(row.ChannelId, []);
            var unreadCount = Math.Max(0, row.MessageCount - row.MessageCountAtRead);

            return new InboxUnreadGroupDto
            {
                Breadcrumb = new InboxBreadcrumbDto
                {
                    GuildId = row.GuildId,
                    GuildName = row.GuildName,
                    GuildIconUrl = GuildIconUrl(row.GuildId),
                    GuildIconThumbnailUrl = GuildIconThumbnailUrl(row.GuildId),
                    CategoryId = row.CategoryId,
                    CategoryName = row.CategoryName,
                    ChannelId = row.ChannelId,
                    ChannelName = row.ChannelName,
                    ChannelType = (int)row.ChannelType,
                    ParentChannelId = row.ParentChannelId,
                    ParentChannelName = row.ParentChannelName,
                },
                LastActivityAt = row.LastActivityAt,
                UnreadCount = unreadCount,
                MentionCount = mentionCounts.GetValueOrDefault(row.ChannelId),
                Previews = messages,
                PreviewsTruncated = unreadCount > messages.Count,
            };
        }).ToList();

        // The cursor comes off the last row of the unfiltered page.
        var last = hasMore ? rows[limit - 1] : null;

        return new InboxUnreadPageDto
        {
            Groups = groups,
            NextCursor = last is null ? null : EncodeCursor(last.LastActivityAt, last.ChannelId),
            PreviewsUnavailable = previewsUnavailable,
        };
    }

    /// <summary>
    /// The header badge: how many channels have something unread, and how many mentions are
    /// waiting.
    /// </summary>
    public async Task<InboxSummaryDto> GetSummaryAsync(string userId)
    {
        var rows = await QueryUnreadAsync(userId, MaxSummaryCount + 1, cursor: null);

        var capped = rows.Count > MaxSummaryCount;
        var page = capped ? rows.Take(MaxSummaryCount).ToList() : rows;

        page = await DropMutedAsync(page);
        page = await DropUnviewableAsync(userId, page);

        var mentionCounts = await CountMentionsAsync(userId, page);

        return new InboxSummaryDto
        {
            UnreadChannelCount = page.Count,
            MentionCount = Math.Min(mentionCounts.Values.Sum(), MaxSummaryCount),
            Capped = capped || mentionCounts.Values.Sum() > MaxSummaryCount,
        };
    }

    /// <summary>The unread predicate, as one query.</summary>
    private async Task<List<UnreadRow>> QueryUnreadAsync(string userId, int take, string? cursor)
    {
        var rows = await BuildUnreadQuery(ctx, userId, cursor)
            .Take(take)
            .ToListAsync();

        // Household modules keep no message history, so "unread" is meaningless for them.
        return rows.Where(r => !r.ChannelType.IsHouseholdModule()).ToList();
    }

    /// <summary>The unread query itself, ordered but not yet paged.</summary>
    internal static IQueryable<UnreadRow> BuildUnreadQuery(
        MicroserviceContext ctx, string userId, string? cursor)
    {
        var query =
            from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join channel in ctx.Channels.AsNoTracking() on member.GuildId equals channel.GuildId
            join readState in ctx.ReadStates.AsNoTracking()
                on new { ChannelId = channel.Id, MemberId = member.Id }
                equals new { readState.ChannelId, readState.MemberId } into readStates
            from readState in readStates.DefaultIfEmpty()
            where channel.LastActivityAt != null
                  && (readState == null
                        ? channel.LastActivityAt > member.JoinedAt
                        : channel.LastActivityAt > readState.LastReadAt)
            select new { member, channel, readState };

        if (cursor is not null && TryDecodeCursor(cursor, out var afterActivity, out var afterChannelId))
        {
            query = query.Where(r =>
                r.channel.LastActivityAt < afterActivity
                || (r.channel.LastActivityAt == afterActivity
                    && string.Compare(r.channel.Id, afterChannelId) > 0));
        }

        return query
            .OrderByDescending(r => r.channel.LastActivityAt)
            .ThenBy(r => r.channel.Id)
            .Select(r => new UnreadRow(
                r.member.Id,
                r.channel.GuildId,
                r.channel.Guild.Name,
                r.channel.Id,
                r.channel.Name,
                r.channel.Type,
                r.channel.CategoryId,
                r.channel.Category != null ? r.channel.Category.Name : null,
                r.channel.ParentChannelId,
                r.channel.ParentChannel != null ? r.channel.ParentChannel.Name : null,
                r.channel.LastActivityAt!.Value,
                r.channel.LastMessageId,
                r.channel.MessageCount,
                r.readState != null ? r.readState.LastReadMessageId : null,
                r.readState != null ? r.readState.MessageCountAtRead : 0,
                r.readState != null ? r.readState.LastReadAt : null,
                r.member.JoinedAt));
    }

    /// <summary>Muted channels, categories and guilds drop out of Unread - the onboarding card
    /// promises "unread messages from all your <b>unmuted</b> channels". They stay in Mentions,
    /// because muting suppresses noise, not a direct ping.</summary>
    private async Task<List<UnreadRow>> DropMutedAsync(List<UnreadRow> rows)
    {
        if (rows.Count == 0) return rows;

        var resolved = await notifications.ResolveForMemberChannelsAsync(
            rows.Select(r => (r.MemberId, r.ChannelId)).ToList());

        return rows
            .Where(r =>
            {
                if (!resolved.TryGetValue((r.MemberId, r.ChannelId), out var settings)) return true;
                return !settings.IsMuted && settings.Level != NotificationLevel.Nothing;
            })
            .ToList();
    }

    /// <summary>Re-checks ViewChannel per channel on the page.</summary>
    private async Task<List<UnreadRow>> DropUnviewableAsync(string userId, List<UnreadRow> rows)
    {
        if (rows.Count == 0) return rows;

        var allowed = new List<UnreadRow>(rows.Count);

        foreach (var row in rows)
        {
            if (await permissions.CanUserPerformActionAsync(userId, row.ChannelId, Permissions.ViewChannel))
                allowed.Add(row);
        }

        return allowed;
    }

    /// <summary>Fetches the preview lines in one batched bus request.</summary>
    private async Task<(Dictionary<string, IReadOnlyList<InboxMessageDto>> Previews, bool Unavailable)>
        LoadPreviewsAsync(List<UnreadRow> rows)
    {
        var previews = new Dictionary<string, IReadOnlyList<InboxMessageDto>>(StringComparer.Ordinal);
        if (rows.Count == 0) return (previews, false);

        try
        {
            var response = await bus.InvokeAsync<GetChannelMessagePagesResponse>(new GetChannelMessagePagesRequest
            {
                Items = rows
                    .Select(r => new ChannelMessagePageQuery
                    {
                        ChannelId = r.ChannelId,
                        AfterMessageId = r.LastReadMessageId,
                    })
                    .ToList(),
                MessagesPerChannel = MaxPreviewMessages,
            });

            foreach (var page in response.Pages)
            {
                previews[page.ChannelId] = page.Messages.Select(Project).ToList();
            }

            return (previews, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load inbox previews; returning unread groups without message bodies");
            return (previews, true);
        }
    }

    /// <summary>
    /// Unread mentions per channel on this page, counted rather than read from a stored tally.
    /// </summary>
    private async Task<Dictionary<string, int>> CountMentionsAsync(string userId, List<UnreadRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows.Count == 0) return counts;

        var channelIds = rows.Select(r => r.ChannelId).ToList();
        var memberIds = rows.Select(r => r.MemberId).Distinct().ToList();

        // The caller's roles, with when they were granted - a broadcast @role only counts if they
        // already held the role when it was sent.
        var roles = await ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => memberIds.Contains(rm.MemberId))
            .Select(rm => new { rm.MemberId, rm.RoleId, rm.CreatedAt, rm.ExpiresAt })
            .ToListAsync();

        var rolesByMember = roles
            .GroupBy(r => r.MemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var broadcasts = await ctx.ChannelBroadcastMentions
            .AsNoTracking()
            .Where(b => channelIds.Contains(b.ChannelId))
            .Select(b => new { b.ChannelId, b.MessageCreatedAt, b.AuthorId, b.Kind, b.RoleId })
            .ToListAsync();

        var suppression = await notifications.ResolveForMemberChannelsAsync(
            rows.Select(r => (r.MemberId, r.ChannelId)).ToList());

        foreach (var row in rows)
        {
            var floor = row.LastReadAt ?? row.JoinedAt;
            suppression.TryGetValue((row.MemberId, row.ChannelId), out var settings);
            var held = rolesByMember.GetValueOrDefault(row.MemberId, []);

            counts[row.ChannelId] = broadcasts.Count(b =>
                b.ChannelId == row.ChannelId
                && b.MessageCreatedAt > floor
                && b.MessageCreatedAt > row.JoinedAt
                && b.AuthorId != userId
                && (b.Kind == BroadcastMentionKind.Everyone
                        ? settings?.SuppressEveryone != true
                        : settings?.SuppressRoleMentions != true
                          && held.Any(r => r.RoleId == b.RoleId
                                           && r.CreatedAt < b.MessageCreatedAt
                                           && (r.ExpiresAt is null || r.ExpiresAt > b.MessageCreatedAt))));
        }

        // Direct and @here mentions live in Messaging's index.
        try
        {
            var response = await bus.InvokeAsync<GetUserMentionsResponse>(new GetUserMentionsRequest
            {
                UserId = userId,
                Since = rows.Min(r => r.LastReadAt ?? r.JoinedAt),
                Limit = MentionCountScanLimit,
            });

            foreach (var mention in response.Mentions)
            {
                if (mention.ChannelId is null) continue;

                var row = rows.FirstOrDefault(r => r.ChannelId == mention.ChannelId);
                if (row is null) continue;
                if (mention.CreatedAt <= (row.LastReadAt ?? row.JoinedAt)) continue;

                counts[mention.ChannelId] = counts.GetValueOrDefault(mention.ChannelId) + 1;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the mention index; unread groups will show broadcast mentions only");
        }

        return counts;
    }

    /// <summary>Ceiling on one mention-index scan.</summary>
    public const int MentionCountScanLimit = 500;

    private static InboxMessageDto Project(InboxMessagePreview preview) => new()
    {
        Id = preview.Id,
        CreatedAt = preview.CreatedAt,
        AuthorId = preview.AuthorId,
        AuthorDisplayName = preview.AuthorDisplayName,
        AuthorAvatarUrl = preview.AuthorAvatarUrl,
        Content = preview.Content,
        IsEncrypted = preview.IsEncrypted,
        MlsGeneration = preview.MlsGeneration,
        Type = preview.Type,
        SystemMessageVariant = preview.SystemMessageVariant,
        EmbedsJson = preview.EmbedsJson,
    };

    // ══════════════════════════════════════════════════════════════════════ Cursor

    private static string EncodeCursor(DateTimeOffset activity, string channelId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{activity.UtcTicks}:{channelId}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset activity, out string channelId)
    {
        activity = default;
        channelId = string.Empty;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = decoded.IndexOf(':');
            if (separator <= 0) return false;

            if (!long.TryParse(decoded[..separator], out var ticks)) return false;

            activity = new DateTimeOffset(ticks, TimeSpan.Zero);
            channelId = decoded[(separator + 1)..];
            return channelId.Length > 0;
        }
        catch (FormatException)
        {
            // A malformed cursor means the client sent something we did not mint.
            return false;
        }
    }
}
