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
///
/// <para><b>Why this is one Postgres query.</b> The naive shape is "for each channel, ask Messaging
/// whether anything is newer" - a cross-service read per channel per open, which for someone in 50
/// guilds is hundreds of round trips to render one popout. Instead the head of each channel is
/// denormalized onto its row by MessageCreatedHandler, so the whole question is answerable from
/// Guild's own tables. Messaging is consulted once, for the message bodies of the page that
/// survived filtering.</para>
///
/// <para><b>Why timestamps, not ids.</b> Id generation moved to ULID, which sorts - but everything
/// minted before that was upper-cased base62, which does not, and there is no backfill. So
/// <c>Channel.LastMessageId &gt; ReadState.LastReadMessageId</c> is wrong for any pre-existing
/// account and always will be. Ids stay opaque cursors handed to Messaging; timestamps decide what
/// is unread.</para>
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

    /// <summary>How many messages are shown under one group. Discord renders a handful, not the
    /// backlog - and the cap is what keeps a channel with 4,000 unread from dominating the
    /// response.</summary>
    public const int MaxPreviewMessages = 5;

    /// <summary>Ceiling on the summary badge. Counting past it would be an unbounded scan for a
    /// number the UI renders as "99+".</summary>
    public const int MaxSummaryCount = 99;

    /// <summary>
    /// The guild's icon route, as the client should call it.
    ///
    /// Built rather than stored: GuildIconController exposes a fixed path per guild and redirects it
    /// to a presigned S3 URL, so there is no column to read and no signed URL to go stale in a
    /// cached response. The public form carries the gateway's <c>/guild</c> prefix, which the proxy
    /// strips before forwarding.
    /// </summary>
    public static string GuildIconUrl(string guildId) => $"/api/v1/guild/guilds/{guildId}/icon";

    public static string GuildIconThumbnailUrl(string guildId) => $"{GuildIconUrl(guildId)}/thumbnail";

    /// <summary>One row of the unread query, before muting and permissions have had their say.</summary>
    private sealed record UnreadRow(
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

        // The cursor comes off the last row of the *unfiltered* page. Deriving it from the filtered
        // one would make a page where everything was muted terminate paging early, hiding
        // everything behind it.
        var last = hasMore ? rows[limit - 1] : null;

        return new InboxUnreadPageDto
        {
            Groups = groups,
            NextCursor = last is null ? null : EncodeCursor(last.LastActivityAt, last.ChannelId),
            PreviewsUnavailable = previewsUnavailable,
        };
    }

    /// <summary>
    /// The header badge: how many channels have something unread, and how many mentions are waiting.
    ///
    /// Runs the same query as the tab, capped, rather than maintaining counters - a counter for this
    /// would have every problem the old MentionCount had, and the cap is what keeps it bounded.
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

    /// <summary>
    /// The unread predicate, as one query.
    ///
    /// <para>The left join is load-bearing: a member who has never opened a channel has no read
    /// state at all, and an inner join would silently drop exactly the channels they have never
    /// looked at - which is most of what belongs in an inbox. Their JoinedAt is the floor instead,
    /// so joining a guild does not surface its entire history as unread.</para>
    /// </summary>
    private async Task<List<UnreadRow>> QueryUnreadAsync(string userId, int take, string? cursor)
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
            select new UnreadRow(
                member.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                channel.ParentChannelId,
                channel.ParentChannel != null ? channel.ParentChannel.Name : null,
                channel.LastActivityAt!.Value,
                channel.LastMessageId,
                channel.MessageCount,
                readState != null ? readState.LastReadMessageId : null,
                readState != null ? readState.MessageCountAtRead : 0,
                readState != null ? readState.LastReadAt : null,
                member.JoinedAt);

        if (cursor is not null && TryDecodeCursor(cursor, out var afterActivity, out var afterChannelId))
        {
            query = query.Where(r =>
                r.LastActivityAt < afterActivity
                || (r.LastActivityAt == afterActivity && string.Compare(r.ChannelId, afterChannelId) > 0));
        }

        var rows = await query
            .OrderByDescending(r => r.LastActivityAt)
            .ThenBy(r => r.ChannelId)
            .Take(take)
            .ToListAsync();

        // Household modules keep no message history, so "unread" is meaningless for them. Filtered
        // here rather than in SQL because the check is an extension method over the enum.
        return rows.Where(r => !r.ChannelType.IsHouseholdModule()).ToList();
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

    /// <summary>
    /// Re-checks ViewChannel per channel on the page.
    ///
    /// Load-bearing rather than defensive: a read state survives losing access to the channel that
    /// produced it, so without this the inbox would keep listing - and previewing the contents of -
    /// a private channel somebody was removed from.
    /// </summary>
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

    /// <summary>
    /// Fetches the preview lines in one batched bus request.
    ///
    /// A failure here degrades rather than propagates: the unread state is Guild's own data and is
    /// still correct without message bodies, so the tab returns its groups with empty previews and a
    /// flag. Returning 500 because a preview fetch timed out would take out a working feature over a
    /// cosmetic one.
    /// </summary>
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
    ///
    /// <para>There used to be a <c>ReadState.MentionCount</c> that the message handler incremented.
    /// It could not survive this design: an @everyone is one row now, not one per member, so there is
    /// no per-user write left to increment - and the counter was never idempotent anyway, so a
    /// Wolverine retry doubled it and a deleted message left it high forever.</para>
    ///
    /// <para>Two sources, both cheap. Broadcast pings are counted in Postgres, in one query for the
    /// whole page, with the role and join-date bounds that make read-time evaluation exact. Direct
    /// and @here mentions come from Messaging's index, in one partition read.</para>
    /// </summary>
    private async Task<Dictionary<string, int>> CountMentionsAsync(string userId, List<UnreadRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows.Count == 0) return counts;

        var channelIds = rows.Select(r => r.ChannelId).ToList();
        var memberIds = rows.Select(r => r.MemberId).Distinct().ToList();

        // The caller's roles, with when they were granted - a broadcast @role only counts if they
        // already held the role when it was sent. Without that bound, being given a role would
        // retroactively fill the inbox with last week's pings.
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

        // Direct and @here mentions live in Messaging's index. A failure there costs the mention
        // half of the badge, not the tab - the broadcast half above is Guild's own data.
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

    /// <summary>Ceiling on one mention-index scan. A raid can produce thousands of rows in the
    /// retention window, and the badge is rendered as "99+" long before that matters.</summary>
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

    // ══════════════════════════════════════════════════════════════════════
    // Cursor
    //
    // Keyset, not offset: the list is ordered by activity and reorders under the reader as messages
    // arrive, so an offset page would skip and repeat rows. Base64 of "ticks:channelId" - opaque to
    // clients by intent, so its shape can change without a wire break.
    // ══════════════════════════════════════════════════════════════════════

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
            // A malformed cursor means the client sent something we did not mint. Treating it as
            // "start from the top" beats a 400: the page is still correct, just not where they
            // expected, and the alternative is a broken tab for a stale bookmark.
            return false;
        }
    }
}
