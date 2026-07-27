using System.Security.Claims;
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
        [NotBody] AuditLogService auditLog, [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var parent = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (parent is null) return Results.NotFound();

        if (parent.Type != ChannelType.Text)
            return Results.BadRequest("Threads can only be created under a Text channel.");

        var canCreate = await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.CreateThreads);
        if (!canCreate) return Results.Forbid();

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

            ctx.Channels.Add(thread);

            auditLog.Log(parent.GuildId, userId, AuditActionType.ChannelCreated, thread.Id, new { ParentChannelId = channelId });

            var presence = await guildHydrateService.GetGuildPresenceAsync(parent.GuildId);
            await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadCreated", new { ChannelId = thread.Id, ParentChannelId = channelId, GuildId = parent.GuildId });

            await bus.PublishAsync(new ThreadCreatedForBots
            {
                ChannelId = thread.Id,
                GuildId = parent.GuildId,
                ParentChannelId = channelId,
                Name = thread.Name,
            });

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

        var threads = await ctx.Channels
            .AsSplitQuery()
            .Where(c => c.ParentChannelId == channelId && c.Type == ChannelType.Thread)
            .OrderByDescending(c => c.CreatedAt)
            .ToFacetsAsync<Channel, ChannelDto>();

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

        // Previously nothing broadcast a thread archive at all, for either audience.
        var presence = await guildHydrateService.GetGuildPresenceAsync(thread.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ThreadUpdated",
            new { ChannelId = thread.Id, ParentChannelId = thread.ParentChannelId, GuildId = thread.GuildId, Archived = true });

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
