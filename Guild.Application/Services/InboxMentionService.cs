using System.Text;
using Guild.Application.Dtos.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

public record MentionFilter
{
    public string? GuildId { get; init; }
    public bool IncludeEveryone { get; init; } = true;
    public bool IncludeRoles { get; init; } = true;
    public bool IncludeDms { get; init; } = true;

    /// <summary>Lookback window.</summary>
    public TimeSpan Since { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>The Mentions tab.</summary>
public class InboxMentionService(
    MicroserviceContext ctx,
    NotificationResolutionService notifications,
    GuildPermissionService permissions,
    IMessageBus bus,
    ILogger<InboxMentionService> logger)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;

    /// <summary>Longest lookback the tab offers, matching the index's retention.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(30);

    private sealed record Candidate(
        string MessageId,
        DateTimeOffset CreatedAt,
        string Kind,
        string AuthorId,
        string? GuildId,
        string? ChannelId,
        string? ConversationId,
        string? RoleId);

    public async Task<InboxMentionPageDto> GetMentionsAsync(
        string userId, MentionFilter filter, int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        var since = DateTimeOffset.UtcNow - (filter.Since > MaxLookback ? MaxLookback : filter.Since);
        TryDecodeCursor(cursor, out var beforeAt, out var beforeId);

        var indexed = await LoadIndexedAsync(userId, filter, since, beforeAt, beforeId, limit);
        var broadcast = await LoadBroadcastAsync(userId, filter, since, beforeAt, beforeId, limit);

        var merged = indexed
            .Concat(broadcast)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.MessageId, StringComparer.Ordinal)
            // A message can be both a direct mention and inside an @everyone.
            .DistinctBy(c => c.MessageId, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var hasMore = merged.Count > limit;
        var page = hasMore ? merged.Take(limit).ToList() : merged;

        page = await DropUnviewableAsync(userId, page);

        var rendered = await RenderAsync(userId, page);

        var last = page.Count > 0 ? page[^1] : null;

        return new InboxMentionPageDto
        {
            Mentions = rendered,
            NextCursor = hasMore && last is not null ? EncodeCursor(last.CreatedAt, last.MessageId) : null,
        };
    }

    private async Task<List<Candidate>> LoadIndexedAsync(
        string userId, MentionFilter filter, DateTimeOffset since,
        DateTimeOffset? beforeAt, string? beforeId, int limit)
    {
        try
        {
            var response = await bus.InvokeAsync<GetUserMentionsResponse>(new GetUserMentionsRequest
            {
                UserId = userId,
                Since = since,
                Before = beforeAt,
                BeforeMessageId = beforeId,
                GuildId = filter.GuildId,
                IncludeDms = filter.IncludeDms,
                Limit = limit + 1,
            });

            return response.Mentions
                .Select(m => new Candidate(
                    m.MessageId, m.CreatedAt, m.Kind, m.AuthorId, m.GuildId, m.ChannelId, m.ConversationId, null))
                .ToList();
        }
        catch (Exception ex)
        {
            // Degrade to broadcast-only rather than failing the tab: those rows are Guild's own data
            // and are still correct.
            logger.LogWarning(ex, "Could not read the mention index; returning broadcast mentions only");
            return [];
        }
    }

    /// <summary>@everyone and @role pings, evaluated against membership at read time.</summary>
    /// <summary>One broadcast ping, before membership and suppression have had their say.</summary>
    internal sealed record BroadcastRow(
        string MessageId,
        DateTimeOffset MessageCreatedAt,
        string AuthorId,
        BroadcastMentionKind Kind,
        string? RoleId,
        string ChannelId,
        string GuildId);

    /// <summary>The broadcast half of the Mentions tab, as one query.</summary>
    internal static IQueryable<BroadcastRow> BuildBroadcastQuery(
        MicroserviceContext ctx,
        string userId,
        IReadOnlyCollection<string> guildIds,
        DateTimeOffset since,
        DateTimeOffset? beforeAt,
        string? beforeId)
    {
        var query = ctx.ChannelBroadcastMentions
            .AsNoTracking()
            .Where(b => guildIds.Contains(b.Channel.GuildId)
                        && b.MessageCreatedAt >= since
                        && b.AuthorId != userId);

        if (beforeAt is not null)
        {
            query = query.Where(b =>
                b.MessageCreatedAt < beforeAt
                || (b.MessageCreatedAt == beforeAt && string.Compare(b.MessageId, beforeId) < 0));
        }

        return query
            .OrderByDescending(b => b.MessageCreatedAt)
            .Select(b => new BroadcastRow(
                b.MessageId, b.MessageCreatedAt, b.AuthorId, b.Kind, b.RoleId, b.ChannelId,
                b.Channel.GuildId));
    }

    private async Task<List<Candidate>> LoadBroadcastAsync(
        string userId, MentionFilter filter, DateTimeOffset since,
        DateTimeOffset? beforeAt, string? beforeId, int limit)
    {
        if (!filter.IncludeEveryone && !filter.IncludeRoles) return [];

        var memberships = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && (filter.GuildId == null || m.GuildId == filter.GuildId))
            .Select(m => new { m.Id, m.GuildId, m.JoinedAt })
            .ToListAsync();

        if (memberships.Count == 0) return [];

        var memberIds = memberships.Select(m => m.Id).ToList();
        var guildIds = memberships.Select(m => m.GuildId).ToList();

        var roles = await ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => memberIds.Contains(rm.MemberId))
            .Select(rm => new { rm.RoleId, rm.CreatedAt, rm.ExpiresAt })
            .ToListAsync();

        var rows = await BuildBroadcastQuery(ctx, userId, guildIds, since, beforeAt, beforeId)
            // Over-fetched because the membership and suppression filters below can reject rows, and
            // a short page would look like the end of the list.
            .Take((limit + 1) * 4)
            .ToListAsync();

        if (rows.Count == 0) return [];

        var joinedByGuild = memberships.ToDictionary(m => m.GuildId, m => m.JoinedAt, StringComparer.Ordinal);

        var suppression = await notifications.ResolveForMemberChannelsAsync(
            rows.Select(r => (memberships.First(m => m.GuildId == r.GuildId).Id, r.ChannelId)).Distinct().ToList());

        var candidates = new List<Candidate>();

        foreach (var row in rows)
        {
            if (row.Kind == BroadcastMentionKind.Everyone && !filter.IncludeEveryone) continue;
            if (row.Kind == BroadcastMentionKind.Role && !filter.IncludeRoles) continue;

            if (!joinedByGuild.TryGetValue(row.GuildId, out var joinedAt)) continue;
            if (row.MessageCreatedAt <= joinedAt) continue;

            var memberId = memberships.First(m => m.GuildId == row.GuildId).Id;
            suppression.TryGetValue((memberId, row.ChannelId), out var settings);

            if (row.Kind == BroadcastMentionKind.Everyone)
            {
                if (settings?.SuppressEveryone == true) continue;
            }
            else
            {
                if (settings?.SuppressRoleMentions == true) continue;

                var held = roles.Any(r => r.RoleId == row.RoleId
                                          && r.CreatedAt < row.MessageCreatedAt
                                          && (r.ExpiresAt is null || r.ExpiresAt > row.MessageCreatedAt));
                if (!held) continue;
            }

            candidates.Add(new Candidate(
                row.MessageId, row.MessageCreatedAt, row.Kind.ToString(), row.AuthorId,
                row.GuildId, row.ChannelId, null, row.RoleId));
        }

        return candidates;
    }

    /// <summary>
    /// Re-checks ViewChannel and ReadMessageHistory per distinct channel on the page.
    /// </summary>
    private async Task<List<Candidate>> DropUnviewableAsync(string userId, List<Candidate> page)
    {
        if (page.Count == 0) return page;

        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<Candidate>(page.Count);

        foreach (var candidate in page)
        {
            // DM mentions have no channel to check; conversation membership gates them instead, and
            // the index only ever contained conversations the caller was in.
            if (candidate.ChannelId is null)
            {
                result.Add(candidate);
                continue;
            }

            if (!allowed.TryGetValue(candidate.ChannelId, out var canRead))
            {
                canRead = await permissions.CanUserPerformActionAsync(userId, candidate.ChannelId, Permissions.ViewChannel)
                          && await permissions.CanUserPerformActionAsync(userId, candidate.ChannelId, Permissions.ReadMessageHistory);

                allowed[candidate.ChannelId] = canRead;
            }

            if (canRead) result.Add(candidate);
        }

        return result;
    }

    /// <summary>Resolves the message bodies and breadcrumbs for one page.</summary>
    private async Task<List<InboxMentionDto>> RenderAsync(string userId, List<Candidate> page)
    {
        if (page.Count == 0) return [];

        var channelIds = page.Where(c => c.ChannelId is not null).Select(c => c.ChannelId!).Distinct().ToList();
        var roleIds = page.Where(c => c.RoleId is not null).Select(c => c.RoleId!).Distinct().ToList();

        var channels = await ctx.Channels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.Name, c.Type, c.GuildId, GuildName = c.Guild.Name, c.CategoryId,
                CategoryName = c.Category != null ? c.Category.Name : null,
                c.ParentChannelId,
                ParentChannelName = c.ParentChannel != null ? c.ParentChannel.Name : null,
            })
            .ToDictionaryAsync(c => c.Id, StringComparer.Ordinal);

        var roleNames = await ctx.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, StringComparer.Ordinal);

        var messages = await LoadMessagesAsync(userId, page);

        var rendered = new List<InboxMentionDto>(page.Count);

        foreach (var candidate in page)
        {
            // Indexed but no longer stored: the message was deleted after being indexed.
            if (!messages.TryGetValue(candidate.MessageId, out var message))
            {
                await ReapAsync(candidate);
                continue;
            }

            InboxBreadcrumbDto? breadcrumb = null;
            if (candidate.ChannelId is not null && channels.TryGetValue(candidate.ChannelId, out var channel))
            {
                breadcrumb = new InboxBreadcrumbDto
                {
                    GuildId = channel.GuildId,
                    GuildName = channel.GuildName,
                    GuildIconUrl = InboxService.GuildIconUrl(channel.GuildId),
                    GuildIconThumbnailUrl = InboxService.GuildIconThumbnailUrl(channel.GuildId),
                    CategoryId = channel.CategoryId,
                    CategoryName = channel.CategoryName,
                    ChannelId = channel.Id,
                    ChannelName = channel.Name,
                    ChannelType = (int)channel.Type,
                    ParentChannelId = channel.ParentChannelId,
                    ParentChannelName = channel.ParentChannelName,
                };
            }

            rendered.Add(new InboxMentionDto
            {
                MessageId = candidate.MessageId,
                CreatedAt = candidate.CreatedAt,
                Kind = candidate.Kind,
                RoleId = candidate.RoleId,
                RoleName = candidate.RoleId is not null ? roleNames.GetValueOrDefault(candidate.RoleId) : null,
                AuthorId = candidate.AuthorId,
                Breadcrumb = breadcrumb,
                ConversationId = candidate.ConversationId,
                Message = message,
            });
        }

        return rendered;
    }

    private async Task<Dictionary<string, InboxMessageDto>> LoadMessagesAsync(string userId, List<Candidate> page)
    {
        var messages = new Dictionary<string, InboxMessageDto>(StringComparer.Ordinal);

        // One lookup per message on the page, issued concurrently.
        var results = await Task.WhenAll(page.Select(async candidate =>
        {
            try
            {
                var response = await bus.InvokeAsync<GetMessageResponse>(
                    new GetMessageRequest { MessageId = candidate.MessageId, RequestingUserId = userId });
                return (candidate.MessageId, response.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve mentioned message {MessageId}", candidate.MessageId);
                return (candidate.MessageId, null);
            }
        }));

        foreach (var (messageId, summary) in results)
        {
            if (summary is null) continue;

            messages[messageId] = new InboxMessageDto
            {
                Id = summary.Id,
                CreatedAt = summary.CreatedAt,
                AuthorId = summary.AuthorId,
                Content = summary.Content,
                EmbedsJson = summary.EmbedsJson,
            };
        }

        return messages;
    }

    private async Task ReapAsync(Candidate candidate)
    {
        // Broadcast rows go with their message on delete, so only index rows can be stale here.
        if (candidate.RoleId is not null || candidate.Kind == nameof(BroadcastMentionKind.Everyone)) return;

        try
        {
            await bus.InvokeAsync<DeleteUserMentionResponse>(new DeleteUserMentionRequest
            {
                UserId = candidate.AuthorId,
                CreatedAt = candidate.CreatedAt,
                MessageId = candidate.MessageId,
            });
        }
        catch (Exception ex)
        {
            // Best effort. The row costs one failed lookup per read until it expires on its own.
            logger.LogDebug(ex, "Could not reap stale mention row for {MessageId}", candidate.MessageId);
        }
    }

    public async Task<bool> DismissAsync(string userId, string messageId, DateTimeOffset createdAt)
    {
        var response = await bus.InvokeAsync<DeleteUserMentionResponse>(new DeleteUserMentionRequest
        {
            UserId = userId,
            CreatedAt = createdAt,
            MessageId = messageId,
        });

        return response.Deleted;
    }

    private static string EncodeCursor(DateTimeOffset at, string messageId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks}:{messageId}"));

    private static void TryDecodeCursor(string? cursor, out DateTimeOffset? at, out string? messageId)
    {
        at = null;
        messageId = null;

        if (string.IsNullOrWhiteSpace(cursor)) return;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = decoded.IndexOf(':');
            if (separator <= 0) return;
            if (!long.TryParse(decoded[..separator], out var ticks)) return;

            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            messageId = decoded[(separator + 1)..];
        }
        catch (FormatException)
        {
            // A cursor we did not mint means a stale bookmark.
        }
    }
}
