using System.Security.Claims;
using Amazon.Runtime;
using Echo.Realtime;
using Facet.Extensions;
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
public class ChannelEndpoint
{
    // Ticket has no behavior implemented behind it yet.
    private static readonly HashSet<ChannelType> CreatableTypes =
        [ChannelType.Text, ChannelType.Voice, ChannelType.Forum, ChannelType.Announcement];

    [WolverinePost("/api/v1/guilds/{guildId}/channels")]
    public async Task<IResult> CreateChannel(string guildId, CreateChannelDto dto,
        [NotBody] GuildPermissionService permissionService,
            [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus,
        [NotBody] ILogger<ChannelEndpoint> logger,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!CreatableTypes.Contains(dto.Type))
            return Results.BadRequest($"Channel type '{dto.Type}' is not creatable directly.");

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        try
        {
            var channel = Channel.Create(new CreateChannelParams()
            {
                Name = dto.Name,
                Type = dto.Type,
                Position = dto.Position,
                Description = dto.Description,
                GuildId = guildId,
                CategoryId = dto.CategoryId,
            });
        
            ctx.Channels.Add(channel);
        
            var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);

            await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ChannelCreated", new
            {
                ChannelId = channel.Id,
                GuildId = channel.GuildId,
            });

            await bus.PublishAsync(new ChannelCreatedForBots
            {
                ChannelId = channel.Id,
                GuildId = channel.GuildId,
                Name = channel.Name,
                Type = channel.Type.ToString(),
                Position = channel.Position,
                CategoryId = channel.CategoryId,
            });



            return Results.Ok(new ChannelDto()
            {
                Type = channel.Type,
                GuildId = guildId,
                Id = channel.Id,
                Name = channel.Name,
                CreatedAt = channel.CreatedAt,
                UpdatedAt = channel.UpdatedAt,
                IsAgeRestricted = channel.IsAgeRestricted,
                IsPrivate = channel.IsPrivate,
            });
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    group => group.Key, 
                    group => group.Select(e => e.ErrorMessage).ToArray()
                );

            return Results.ValidationProblem(errors);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not create channel");
            return Results.InternalServerError();
        }
        
    }

    [WolverineDelete("/api/v1/channels/{channelId}")]

    public async Task<IResult> DeleteChannelAsync(string channelId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] AuditLogService auditLog,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, channel.GuildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        ctx.Channels.Remove(channel);

        auditLog.Log(channel.GuildId, userId, AuditActionType.ChannelDeleted, channelId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(channel.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ChannelDeleted", new { ChannelId = channel.Id, GuildId = channel.GuildId });

        await bus.PublishAsync(new ChannelDeletedForBots { ChannelId = channel.Id, GuildId = channel.GuildId });

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/channels/{channelId}")]
    public async Task<IResult> UpdateChannelAsync(string channelId, UpdateChannelDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] AuditLogService auditLog,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, channel.GuildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        try
        {
            channel.Update(new Channel.UpdateChannelParams
            {
                Name = dto.Name,
                Description = dto.Description,
                IsAgeRestricted = dto.IsAgeRestricted,
                IsPrivate = dto.IsPrivate,
                SlowModeSeconds = dto.SlowModeSeconds,
            });
        }
        catch (ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }

        auditLog.Log(channel.GuildId, userId, AuditActionType.ChannelUpdated, channelId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(channel.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ChannelUpdated", new { ChannelId = channel.Id, GuildId = channel.GuildId });

        await bus.PublishAsync(new ChannelUpdatedForBots
        {
            ChannelId = channel.Id,
            GuildId = channel.GuildId,
            Name = channel.Name,
            Type = channel.Type.ToString(),
            Position = channel.Position,
            CategoryId = channel.CategoryId,
        });

        return Results.Ok(new ChannelDto
        {
            Type = channel.Type,
            GuildId = channel.GuildId,
            Id = channel.Id,
            Name = channel.Name,
            CreatedAt = channel.CreatedAt,
            UpdatedAt = channel.UpdatedAt,
            IsAgeRestricted = channel.IsAgeRestricted,
            IsPrivate = channel.IsPrivate,
        });
    }


    [WolverinePatch("/api/v1/guilds/{guildId}/channels/reorder")]
    public async Task<IResult> ReorderChannels(
        string guildId,
        ReorderChannelsDto dto,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        if (dto.Categories.Count > 0)
        {
            var categoryIds = dto.Categories.Select(c => c.CategoryId).ToList();
            var categories = await ctx.Categories
                .Where(c => c.GuildId == guildId && categoryIds.Contains(c.Id))
                .ToListAsync();

            if (categories.Count != categoryIds.Count)
                return Results.BadRequest("One or more categories not found in this guild.");

            var categoryPositionById = dto.Categories.ToDictionary(c => c.CategoryId, c => c.Position);
            foreach (var category in categories)
                category.Position = categoryPositionById[category.Id];
        }

        if (dto.Channels.Count > 0)
        {
            var channelIds = dto.Channels.Select(c => c.ChannelId).ToList();
            var channels = await ctx.Channels
                .Where(c => c.GuildId == guildId && channelIds.Contains(c.Id))
                .ToListAsync();

            if (channels.Count != channelIds.Count)
                return Results.BadRequest("One or more channels not found in this guild.");

            // Validate that any referenced category IDs belong to this guild.
            var referencedCategoryIds = dto.Channels
                .Where(c => c.CategoryId != null)
                .Select(c => c.CategoryId!)
                .Distinct()
                .ToList();

            if (referencedCategoryIds.Count > 0)
            {
                var validCategoryIds = await ctx.Categories
                    .Where(c => c.GuildId == guildId && referencedCategoryIds.Contains(c.Id))
                    .Select(c => c.Id)
                    .ToListAsync();

                if (validCategoryIds.Count != referencedCategoryIds.Count)
                    return Results.BadRequest("One or more referenced categories not found in this guild.");
            }

            var channelUpdatesById = dto.Channels.ToDictionary(c => c.ChannelId);
            foreach (var channel in channels)
            {
                var update = channelUpdatesById[channel.Id];
                channel.Position = update.Position;
                channel.CategoryId = update.CategoryId;
            }
        }
        
        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.ChannelReordered", dto);

        return Results.NoContent();
    }
}