using System.Security.Claims;
using Echo.Realtime;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The Inbox: unread channels and mentions across every guild the caller is in.
///
/// <para>Lives in Guild rather than Messaging because Guild already owns four of the five inputs -
/// read state, channels, categories, and mute/permission resolution. Only the message bodies come
/// from Messaging, over the bus, once per page.</para>
///
/// <para>Declared under <c>/api/v1/inbox</c>; the gateway rewrites <c>/api/v1/guild/{**}</c> to
/// <c>/api/v1/{**}</c>, so the public paths are <c>/api/v1/guild/inbox/...</c>. No gateway change
/// was needed.</para>
/// </summary>
[Authorize]
public class InboxEndpoint
{
    [WolverineGet("/api/v1/inbox/unread")]
    public async Task<IResult> GetUnread(
        [NotBody] ClaimsPrincipal user,
        [NotBody] InboxService inbox,
        int? limit = null,
        string? cursor = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var page = await inbox.GetUnreadAsync(userId, limit ?? InboxService.DefaultPageSize, cursor);

        return Results.Ok(page);
    }

    /// <summary>The header badge.</summary>
    [WolverineGet("/api/v1/inbox/summary")]
    public async Task<IResult> GetSummary(
        [NotBody] ClaimsPrincipal user,
        [NotBody] InboxService inbox)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        return Results.Ok(await inbox.GetSummaryAsync(userId));
    }

    /// <summary>
    /// The Mentions tab. Merges Messaging's per-user index (direct and @here) with Guild's own
    /// broadcast rows (@everyone and @role), which are evaluated against membership here rather than
    /// fanned out when they were sent.
    /// </summary>
    [WolverineGet("/api/v1/inbox/mentions")]
    public async Task<IResult> GetMentions(
        [NotBody] ClaimsPrincipal user,
        [NotBody] InboxMentionService mentions,
        string? guildId = null,
        string? since = null,
        bool includeEveryone = true,
        bool includeRoles = true,
        bool includeDms = true,
        int? limit = null,
        string? cursor = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var filter = new MentionFilter
        {
            GuildId = guildId,
            IncludeEveryone = includeEveryone,
            IncludeRoles = includeRoles,
            IncludeDms = includeDms,
            Since = ParseLookback(since),
        };

        var page = await mentions.GetMentionsAsync(
            userId, filter, limit ?? InboxMentionService.DefaultPageSize, cursor);

        return Results.Ok(page);
    }

    /// <summary>Dismisses one mention - the X in the UI. Idempotent: dismissing twice is 204 both
    /// times, because the caller has nothing useful to do with the difference.</summary>
    [WolverineDelete("/api/v1/inbox/mentions/{messageId}")]
    public async Task<IResult> DismissMention(
        string messageId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] InboxMentionService mentions,
        string? createdAt = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // The index is clustered on (created_at, message_id), so the timestamp is part of the key -
        // deleting by message id alone is not a query the store can answer.
        if (!DateTimeOffset.TryParse(createdAt, out var at))
            return Results.BadRequest("createdAt is required and must be an ISO-8601 timestamp.");

        await mentions.DismissAsync(userId, messageId, at);

        return Results.NoContent();
    }

    /// <summary>The three windows the tab offers, matching Discord's. Anything else falls back to
    /// the default rather than erroring - a bad value is a stale client, not an attack.</summary>
    private static TimeSpan ParseLookback(string? since) => since switch
    {
        "24h" => TimeSpan.FromHours(24),
        "30d" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7),
    };

    /// <summary>
    /// REST twin of the existing <c>guild.UpdateLastRead</c> hub method.
    ///
    /// Both funnel into the same handler. This exists so the check button works without a live hub
    /// connection - and so the behaviour is reachable from an E2E test, which a hub-only path is
    /// not.
    /// </summary>
    [WolverinePost("/api/v1/inbox/channels/{channelId}/read")]
    public async Task<IResult> MarkChannelRead(
        string channelId,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext context,
        [NotBody] Wolverine.IMessageBus bus,
        [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var channel = await context.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.Id, c.GuildId, c.LastMessageId })
            .FirstOrDefaultAsync();

        if (channel is null) return Results.NotFound();

        var isMember = await context.GuildMembers
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.GuildId == channel.GuildId);

        if (!isMember) return Results.Forbid();

        // Acking a channel with no messages is a no-op rather than an error: the client may be
        // marking a whole guild read and not know which channels are empty.
        if (channel.LastMessageId is null) return Results.NoContent();

        // Same command the hub's guild.UpdateLastRead sends, invoked rather than sent so the
        // response only returns once the cursor has actually moved - a REST caller that gets 204
        // and then re-reads must not see the old state.
        await bus.InvokeAsync(new UpdateGuildReadCommand(userId, channelId, channel.LastMessageId));

        await hub.Clients.User(userId).SendAsync("inbox.ReadStateChanged", new
        {
            ChannelId = channelId,
            LastReadMessageId = channel.LastMessageId,
            MentionCount = 0,
        });

        return Results.NoContent();
    }

    /// <summary>
    /// Marks every unread channel read at once - the header's check button.
    ///
    /// <para>Set-based by necessity. Someone in 200 guilds across 50 channels each is 10,000 rows;
    /// a per-channel loop would be 10,000 round trips, which is the shape this whole feature exists
    /// to avoid. Existing read states are bulk-updated from the channel row they point at, and the
    /// channels that have never been opened get rows inserted in one batch.</para>
    /// </summary>
    [WolverinePost("/api/v1/inbox/read-all")]
    public async Task<IResult> MarkAllRead(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext context,
        [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var memberIds = await context.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Id)
            .ToListAsync();

        if (memberIds.Count == 0) return Results.NoContent();

        // Every channel the caller could have unread in, with its head. One query for every guild
        // they are in.
        var channels = await (
            from member in context.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join channel in context.Channels.AsNoTracking() on member.GuildId equals channel.GuildId
            where channel.LastActivityAt != null
            select new
            {
                MemberId = member.Id,
                ChannelId = channel.Id,
                channel.LastMessageId,
                channel.LastActivityAt,
                channel.MessageCount,
            }).ToListAsync();

        if (channels.Count == 0) return Results.NoContent();

        var existing = await context.ReadStates
            .Where(rs => memberIds.Contains(rs.MemberId))
            .ToListAsync();

        var byKey = existing.ToDictionary(rs => (rs.MemberId, rs.ChannelId));
        var now = DateTime.UtcNow;

        foreach (var channel in channels)
        {
            if (byKey.TryGetValue((channel.MemberId, channel.ChannelId), out var readState))
            {
                readState.LastReadMessageId = channel.LastMessageId;
                readState.LastReadAt = channel.LastActivityAt;
                readState.MessageCountAtRead = channel.MessageCount;
                readState.UpdatedAt = now;
                continue;
            }

            context.ReadStates.Add(new ReadState
            {
                Id = ReadState.GenerateId(),
                CreatedAt = now,
                UpdatedAt = now,
                MemberId = channel.MemberId,
                ChannelId = channel.ChannelId,
                LastReadMessageId = channel.LastMessageId,
                LastReadAt = channel.LastActivityAt,
                MessageCountAtRead = channel.MessageCount,
            });
        }

        await context.SaveChangesAsync();

        await hub.Clients.User(userId).SendAsync("inbox.ReadStateChanged", new { All = true });

        return Results.NoContent();
    }
}
