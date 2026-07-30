using System.Security.Claims;
using System.Text;
using Echo.Realtime;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using FluentValidation;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class ThreadEndpoint
{
    [WolverinePost("/api/v1/channels/{channelId}/threads")]
    public async Task<IResult> CreateThreadAsync(string channelId, CreateThreadDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] AuditLogService auditLog, [NotBody] ForumService forumService,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var parent = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (parent is null) return Results.NotFound();

        // A Forum channel's "posts" are threads with a Forum parent instead of a Text one - same
        // entity, same permission model, same listing endpoint below. dto.Name doubles as the post
        // title in that case. Media channels are forums that render differently, nothing more.
        if (parent.Type != ChannelType.Text && !parent.Type.IsForum())
            return Results.BadRequest("Threads can only be created under a Text, Forum or Media channel.");

        var canCreate = await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.CreateThreads);
        if (!canCreate) return Results.Forbid();

        var isForumPost = parent.Type.IsForum();
        var config = isForumPost ? await forumService.GetConfigAsync(channelId, parent.GuildId) : null;

        try
        {
            var thread = Channel.Create(new CreateChannelParams
            {
                Name = dto.Name,
                Description = dto.Description ?? "",
                Type = ChannelType.Thread,
                GuildId = parent.GuildId,
                ParentChannelId = channelId,
                CreatedByUserId = userId,
            });

            if (config is not null)
            {
                // Snapshotted onto the post, not read through to the forum on every send: a later
                // change to the forum default shouldn't retroactively slow down live posts.
                thread.SlowModeSeconds = config.DefaultThreadSlowModeSeconds;
                thread.AutoArchiveMinutes = config.DefaultAutoArchiveMinutes;
                thread.AutoArchiveAt = DateTimeOffset.UtcNow.AddMinutes(config.DefaultAutoArchiveMinutes);
            }

            ctx.Channels.Add(thread);

            List<string> appliedTagIds = [];

            if (isForumPost)
            {
                var isModerator = await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.ManageChannel)
                                  || await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.ManageAnyThread);

                var tagResult = await forumService.SetPostTagsAsync(
                    thread.Id, channelId, dto.TagIds, isModerator, config!.RequireTag);

                // Reject the whole create rather than dropping the offending tags - a post that
                // silently loses its tags is worse than one the author has to retry.
                if (tagResult.Forbidden) return Results.Forbid();
                if (!tagResult.Succeeded) return Results.BadRequest(tagResult.Error);

                appliedTagIds = tagResult.TagIds!;
            }

            auditLog.Log(parent.GuildId, userId, AuditActionType.ChannelCreated, thread.Id, new { ParentChannelId = channelId });

            var presence = await guildHydrateService.GetGuildPresenceAsync(parent.GuildId);
            await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadCreated", new { ChannelId = thread.Id, ParentChannelId = channelId, GuildId = parent.GuildId, TagIds = appliedTagIds });

            await bus.PublishAsync(new ThreadCreatedForBots
            {
                ChannelId = thread.Id,
                GuildId = parent.GuildId,
                ParentChannelId = channelId,
                Name = thread.Name,
            });

            // Forum "posts" are just threads that open with a message - a Forum-parented thread
            // with no body would otherwise render as an empty post.
            if (!string.IsNullOrWhiteSpace(dto.Content))
            {
                await bus.InvokeAsync(new CreateMessageCommand
                {
                    Content = Encoding.UTF8.GetBytes(dto.Content),
                    ChannelId = thread.Id,
                    AuthorId = userId,
                    AuthorIdType = AuthorIdType.User,
                    Mentions = [],
                });
            }

            return Results.Ok(new ChannelDto
            {
                Type = thread.Type,
                GuildId = thread.GuildId,
                Id = thread.Id,
                Name = thread.Name,
                Description = thread.Description,
                CreatedAt = thread.CreatedAt,
                UpdatedAt = thread.UpdatedAt,
                IsAgeRestricted = thread.IsAgeRestricted,
                IsPrivate = thread.IsPrivate,
                ParentChannelId = thread.ParentChannelId,
                CreatedByUserId = thread.CreatedByUserId,
                IsArchived = thread.IsArchived,
            });
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }
    }

    [WolverineGet("/api/v1/channels/{channelId}/threads")]
    public async Task<IResult> GetThreadsAsync(string channelId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.ViewChannel);
        if (!canView) return Results.Forbid();

        // Built manually rather than via ToFacetsAsync<Channel, ChannelDto>() - ChannelDto's
        // NestedFacets include GuildDto, which itself nests Channels/Categories/Roles
        // (MaxDepth = 1); materializing that whole graph just to list threads previously threw
        // ("Required nested facet property 'Guild' on source type was null") once a guild had a
        // thread in it, a genuine pre-existing bug this fixes rather than works around.
        // Capped at 50: this previously returned every thread in the channel unbounded, which is
        // fine for a text-channel thread sidebar and a cliff for a busy forum. Forum clients
        // should use GET /channels/{forumId}/posts instead - it filters, sorts and pages.
        var threads = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.ParentChannelId == channelId && c.Type == ChannelType.Thread)
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .Select(c => new ChannelDto
            {
                Id = c.Id,
                Type = c.Type,
                GuildId = c.GuildId,
                Name = c.Name,
                Description = c.Description,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsAgeRestricted = c.IsAgeRestricted,
                IsPrivate = c.IsPrivate,
                ParentChannelId = c.ParentChannelId,
                CreatedByUserId = c.CreatedByUserId,
                IsArchived = c.IsArchived,
            })
            .ToListAsync();

        return Results.Ok(threads);
    }

    [WolverinePatch("/api/v1/threads/{threadId}/archive")]
    public async Task<IResult> ArchiveThreadAsync(string threadId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var thread = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == threadId && c.Type == ChannelType.Thread);
        if (thread is null) return Results.NotFound();

        var requiredPermission = thread.CreatedByUserId == userId
            ? Permissions.ManageOwnThreads
            : Permissions.ManageAnyThread;

        var canArchive = await permissionService.CanUserPerformActionAsync(userId, threadId, requiredPermission);
        if (!canArchive) return Results.Forbid();

        thread.IsArchived = true;

        auditLog.Log(thread.GuildId, userId, AuditActionType.ChannelUpdated, threadId, new { Archived = true });

        // Previously nothing broadcast a thread archive at all, for either audience. The payload
        // carries the post's full flag state (not just the flag that changed) so one client
        // handler can treat every guild.ThreadUpdated as a replace - see ForumPostEndpoint, which
        // emits the same shape for tag/pin/lock changes.
        var tagIds = await ctx.ForumPostTags.AsNoTracking()
            .Where(pt => pt.ThreadChannelId == threadId)
            .Select(pt => pt.TagId)
            .ToListAsync();

        var presence = await guildHydrateService.GetGuildPresenceAsync(thread.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadUpdated",
            new
            {
                ChannelId = thread.Id,
                ParentChannelId = thread.ParentChannelId,
                GuildId = thread.GuildId,
                Name = thread.Name,
                TagIds = tagIds,
                IsPinned = thread.IsPinned,
                IsLocked = thread.IsLocked,
                Archived = true,
            });

        await bus.PublishAsync(new ThreadUpdatedForBots
        {
            ChannelId = thread.Id,
            GuildId = thread.GuildId,
            ParentChannelId = thread.ParentChannelId ?? "",
            Name = thread.Name,
            Archived = true,
        });

        return Results.NoContent();
    }
}
