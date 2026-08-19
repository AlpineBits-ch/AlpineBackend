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
using MessagingAuthorIdType = Messaging.Contracts.Bus.Commands.AuthorIdType;

namespace Guild.Application.Endpoints;

[Authorize]
public class ThreadEndpoint
{
    /// <summary>Creates a thread, or a forum post - the same entity either way.</summary>
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

            // Added only once nothing above can still reject: AutoApplyTransactions commits this
            // context when the endpoint returns and does not look at the IResult, so an earlier Add
            // would persist the post the tag check just refused. SetPostTagsAsync stages nothing on
            // either of its failure paths, so this ordering leaves a rejected create with no rows.
            ctx.Channels.Add(thread);

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
                    AuthorIdType = MessagingAuthorIdType.User,
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
                StarterMessageId = thread.StarterMessageId,
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

    /// <summary>Starts a thread from an existing message, which stays where it was posted.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/messages/{messageId}/threads")]
    public async Task<IResult> CreateThreadFromMessageAsync(string channelId, string messageId,
        CreateThreadDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] AuditLogService auditLog,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var parent = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (parent is null) return Results.NotFound();

        // Text only. A forum post already is the thread, and a thread-shaped parent would nest -
        // both are covered by the plain-channel route above.
        if (parent.Type != ChannelType.Text)
            return Results.BadRequest("A thread can only be started from a message in a Text channel.");

        // Channel.Create never sets EncryptionState, so a thread under an MLS channel would be a
        // plaintext room hanging off an encrypted one. Refused rather than silently downgraded.
        if (parent.EncryptionState != EncryptionState.Plain)
            return Results.BadRequest("Threads are not available in an encrypted channel.");

        var canCreate = await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.CreateThreads);
        if (!canCreate) return Results.Forbid();

        // Cheap enough to check before the round trip, though the unique index is what actually
        // decides the race between two people clicking the same button. Scoped to this parent so a
        // message id from a guild the caller cannot see resolves to nothing here and is refused by
        // Messaging below, rather than being confirmed by a 409 carrying someone else's thread.
        var existing = await ctx.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.StarterMessageId == messageId && c.ParentChannelId == channelId);
        if (existing is not null)
            return Results.Conflict(new ThreadConflictDto { ThreadId = existing.Id });

        Channel thread;
        try
        {
            // In memory only - nothing may reach the context until the attach below has succeeded.
            thread = Channel.Create(new CreateChannelParams
            {
                Name = dto.Name,
                Description = dto.Description ?? "",
                Type = ChannelType.Thread,
                GuildId = parent.GuildId,
                ParentChannelId = channelId,
                CreatedByUserId = userId,
                StarterMessageId = messageId,
            });
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }

        // Messaging owns the message and is the only service that can say whether it exists and
        // sits in this channel, so the answer has to come back before anything is written here.
        var attach = await bus.InvokeAsync<AttachThreadToMessageResponse>(new AttachThreadToMessageCommand
        {
            MessageId = messageId,
            ChannelId = channelId,
            ThreadId = thread.Id,
        });

        switch (attach.Outcome)
        {
            case AttachThreadOutcome.MessageNotFound:
            case AttachThreadOutcome.WrongChannel:
                return Results.NotFound();
            case AttachThreadOutcome.AlreadyHasThread:
                return Results.Conflict(new ThreadConflictDto { ThreadId = attach.ExistingThreadId });
        }

        ctx.Channels.Add(thread);

        auditLog.Log(parent.GuildId, userId, AuditActionType.ChannelCreated, thread.Id,
            new { ParentChannelId = channelId, StarterMessageId = messageId });

        var presence = await guildHydrateService.GetGuildPresenceAsync(parent.GuildId);
        var audience = presence.Select(p => p.UserId).ToList();

        await hub.Clients.Users(audience).SendAsync("guild.ThreadCreated",
            new { ChannelId = thread.Id, ParentChannelId = channelId, GuildId = parent.GuildId, TagIds = Array.Empty<string>() });

        // Separate from ThreadCreated: a client showing the parent channel has to redraw one
        // message it already has, which is not what a new-thread-in-the-list event means.
        await hub.Clients.Users(audience).SendAsync("guild.MessageThreadAttached",
            new { ChannelId = channelId, GuildId = parent.GuildId, MessageId = messageId, ThreadId = thread.Id, Name = thread.Name });

        await bus.PublishAsync(new ThreadCreatedForBots
        {
            ChannelId = thread.Id,
            GuildId = parent.GuildId,
            ParentChannelId = channelId,
            Name = thread.Name,
            StarterMessageId = messageId,
        });

        if (!string.IsNullOrWhiteSpace(dto.Content))
        {
            await bus.InvokeAsync(new CreateMessageCommand
            {
                Content = Encoding.UTF8.GetBytes(dto.Content),
                ChannelId = thread.Id,
                AuthorId = userId,
                AuthorIdType = MessagingAuthorIdType.User,
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
            StarterMessageId = thread.StarterMessageId,
        });
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
        // NestedFacets include GuildDto, which itself nests Channels/Categories/Roles (MaxDepth =
        // 1); materializing that whole graph just to list threads previously threw ("Required
        // nested facet property 'Guild' on source type was null") once a guild had a thread in it,
        // a genuine pre-existing bug this fixes rather than works around.
        var threads = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.ParentChannelId == channelId && ChannelTypeExtensions.ThreadShaped.Contains(c.Type))
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
                StarterMessageId = c.StarterMessageId,
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

        // Scenes archive through here too: a scene the archive route did not recognise would escape
        // ManageOwnThreads and ManageAnyThread entirely.
        var thread = await ctx.Channels.FirstOrDefaultAsync(
            c => c.Id == threadId && ChannelTypeExtensions.ThreadShaped.Contains(c.Type));
        if (thread is null) return Results.NotFound();

        var requiredPermission = thread.CreatedByUserId == userId
            ? Permissions.ManageOwnThreads
            : Permissions.ManageAnyThread;

        var canArchive = await permissionService.CanUserPerformActionAsync(userId, threadId, requiredPermission);
        if (!canArchive) return Results.Forbid();

        thread.IsArchived = true;

        auditLog.Log(thread.GuildId, userId, AuditActionType.ChannelUpdated, threadId, new { Archived = true });

        // Previously nothing broadcast a thread archive at all, for either audience.
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
