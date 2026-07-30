using System.Security.Claims;
using Echo.Realtime;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>Reading and moderating forum posts.</summary>
[Authorize]
public class ForumPostEndpoint
{
    private const int MaxPageSize = 50;
    private const int DefaultPageSize = 25;

    [WolverineGet("/api/v1/channels/{forumId}/posts")]
    public async Task<IResult> ListPostsAsync(string forumId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] ClaimsPrincipal user,
        string? tagIds = null, string? match = null, string? sort = null,
        string? archived = null, int? limit = null, string? cursor = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ViewChannel))
            return Results.Forbid();

        var config = await forumService.GetConfigAsync(forumId, forum.GuildId);

        var sortOrder = sort?.ToLowerInvariant() switch
        {
            "activity" => ForumSortOrder.LatestActivity,
            "created" => ForumSortOrder.CreationDate,
            null or "" => config.DefaultSortOrder,
            _ => (ForumSortOrder?)null,
        };
        if (sortOrder is null) return Results.BadRequest("sort must be 'activity' or 'created'.");

        var matchAll = match?.ToLowerInvariant() switch
        {
            "all" => true,
            "any" or null or "" => false,
            _ => (bool?)null,
        };
        if (matchAll is null) return Results.BadRequest("match must be 'any' or 'all'.");

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = ctx.Channels.AsNoTracking()
            .Where(c => c.ParentChannelId == forumId && c.Type == ChannelType.Thread);

        query = archived?.ToLowerInvariant() switch
        {
            "true" => query.Where(c => c.IsArchived),
            "all" => query,
            _ => query.Where(c => !c.IsArchived),
        };

        var requestedTagIds = (tagIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();

        if (requestedTagIds.Count > 0)
        {
            var applications = ctx.ForumPostTags.AsNoTracking().Where(pt => requestedTagIds.Contains(pt.TagId));

            // match=all as a grouped count rather than N chained EXISTS: one index scan on
            // (tag_id), and the cost stays flat as the client adds chips.
            var matchingPostIds = matchAll.Value
                ? applications.GroupBy(pt => pt.ThreadChannelId)
                    .Where(g => g.Count() == requestedTagIds.Count)
                    .Select(g => g.Key)
                : applications.Select(pt => pt.ThreadChannelId).Distinct();

            query = query.Where(c => matchingPostIds.Contains(c.Id));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!ForumService.TryDecodeCursor(cursor, out var curPinned, out var curKey, out var curId))
                return Results.BadRequest("Malformed cursor.");

            // Keyset predicate over the full ordering tuple (pinned, sortKey, id), descending.
            query = sortOrder == ForumSortOrder.LatestActivity
                ? query.Where(c =>
                    (curPinned && !c.IsPinned) ||
                    (c.IsPinned == curPinned &&
                        ((c.LastActivityAt ?? c.CreatedAt) < curKey ||
                         ((c.LastActivityAt ?? c.CreatedAt) == curKey && string.Compare(c.Id, curId) < 0))))
                : query.Where(c =>
                    (curPinned && !c.IsPinned) ||
                    (c.IsPinned == curPinned &&
                        (c.CreatedAt < curKey ||
                         (c.CreatedAt == curKey && string.Compare(c.Id, curId) < 0))));
        }

        query = sortOrder == ForumSortOrder.LatestActivity
            ? query.OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastActivityAt ?? c.CreatedAt)
                .ThenByDescending(c => c.Id)
            : query.OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id);

        // One extra row is the cheapest "is there a next page" - no count query, and it decides
        // whether NextCursor is null without a second round trip.
        var rows = await query.Take(pageSize + 1).ToListAsync();

        var hasMore = rows.Count > pageSize;
        var posts = hasMore ? rows.Take(pageSize).ToList() : rows;

        var tagsByPost = await forumService.GetTagIdsForPostsAsync(posts.Select(p => p.Id).ToList());

        var last = posts.LastOrDefault();

        return Results.Ok(new ForumPostPageDto
        {
            Posts = posts.Select(p => ForumPostDto.From(p, tagsByPost.GetValueOrDefault(p.Id))).ToList(),
            NextCursor = hasMore && last is not null
                ? ForumService.EncodeCursor(last.IsPinned, ForumService.SortKey(last, sortOrder.Value), last.Id)
                : null,
        });
    }

    [WolverinePut("/api/v1/threads/{threadId}/tags")]
    public async Task<IResult> SetTagsAsync(string threadId, SetThreadTagsDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var post = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == threadId && c.Type == ChannelType.Thread);
        if (post is null || post.ParentChannelId is null) return Results.NotFound();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == post.ParentChannelId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        var isModerator = await permissionService.CanUserPerformActionAsync(userId, threadId, Permissions.ManageAnyThread)
                          || await permissionService.CanUserPerformActionAsync(userId, forum.Id, Permissions.ManageChannel);

        // The author can retag their own post; anyone else needs ManageAnyThread.
        if (post.CreatedByUserId != userId && !isModerator) return Results.Forbid();

        var config = await forumService.GetConfigAsync(forum.Id, forum.GuildId);

        var result = await forumService.SetPostTagsAsync(threadId, forum.Id, dto.TagIds, isModerator, config.RequireTag);

        if (result.Forbidden) return Results.Forbid();
        if (!result.Succeeded) return Results.BadRequest(result.Error);

        auditLog.Log(post.GuildId, userId, AuditActionType.ThreadTagsUpdated, threadId, new { TagIds = result.TagIds });

        await BroadcastThreadUpdatedAsync(hub, guildHydrateService, bus, post, result.TagIds!);

        return Results.Ok(ForumPostDto.From(post, result.TagIds));
    }

    [WolverinePatch("/api/v1/threads/{threadId}/pin")]
    public async Task<IResult> SetPinnedAsync(string threadId, SetThreadPinnedDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var post = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == threadId && c.Type == ChannelType.Thread);
        if (post is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, threadId, Permissions.ManageAnyThread))
            return Results.Forbid();

        post.IsPinned = dto.Pinned;

        auditLog.Log(post.GuildId, userId, AuditActionType.ThreadPinChanged, threadId, new { dto.Pinned });

        var tagsByPost = await forumService.GetTagIdsForPostsAsync([threadId]);
        await BroadcastThreadUpdatedAsync(hub, guildHydrateService, bus, post, tagsByPost.GetValueOrDefault(threadId, []));

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/threads/{threadId}/lock")]
    public async Task<IResult> SetLockedAsync(string threadId, SetThreadLockedDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var post = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == threadId && c.Type == ChannelType.Thread);
        if (post is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, threadId, Permissions.ManageAnyThread))
            return Results.Forbid();

        post.IsLocked = dto.Locked;

        auditLog.Log(post.GuildId, userId, AuditActionType.ThreadLockChanged, threadId, new { dto.Locked });

        var tagsByPost = await forumService.GetTagIdsForPostsAsync([threadId]);
        await BroadcastThreadUpdatedAsync(hub, guildHydrateService, bus, post, tagsByPost.GetValueOrDefault(threadId, []));

        return Results.NoContent();
    }

    /// <summary>Tag changes, pins and locks all surface as guild.ThreadUpdated rather than as
    /// their own events, carrying the full current flag state - one client handler updates the
    /// post card for any of them, and the payload is a replace rather than a patch.</summary>
    private static async Task BroadcastThreadUpdatedAsync(IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService guildHydrateService, IMessageBus bus, Channel post, List<string> tagIds)
    {
        var presence = await guildHydrateService.GetGuildPresenceAsync(post.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadUpdated", new
        {
            ChannelId = post.Id,
            ParentChannelId = post.ParentChannelId,
            GuildId = post.GuildId,
            Name = post.Name,
            TagIds = tagIds,
            IsPinned = post.IsPinned,
            IsLocked = post.IsLocked,
            Archived = post.IsArchived,
        });

        await bus.PublishAsync(new ThreadUpdatedForBots
        {
            ChannelId = post.Id,
            GuildId = post.GuildId,
            ParentChannelId = post.ParentChannelId ?? "",
            Name = post.Name,
            Archived = post.IsArchived,
        });
    }
}
