using System.Security.Claims;
using Echo.Realtime;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// Class-level [Authorize] as defence in depth. Every handler here already resolves the caller and
/// 401s on a missing claim, but Guild registers no fallback authorization policy, so without this
/// an anonymous request reached the handler body and did a database read before being turned away.
/// </summary>
[Authorize]
public class CategoryEndpoint
{
    // Discord's gateway protocol has no separate "category" entity - a category is just a
    // channel with type:4, and Bots.Application's dispatch/DiscordChannelType already expect
    // that (see the Discord import sync handler's "Categories are just type:4 channels" comment).
    // Reusing the existing Channel*ForBots contracts here, rather than adding parallel
    // Category-specific ones, keeps installed bots' view of category create/update/delete
    // consistent with real Discord without a second dispatch path to maintain.
    private static ChannelCreatedForBots ToBotsCreatedEvent(Category category) => new()
    {
        ChannelId = category.Id,
        GuildId = category.GuildId,
        Name = category.Name,
        Type = "Category",
        Position = category.Position,
        CategoryId = null,
    };

    private static ChannelUpdatedForBots ToBotsUpdatedEvent(Category category) => new()
    {
        ChannelId = category.Id,
        GuildId = category.GuildId,
        Name = category.Name,
        Type = "Category",
        Position = category.Position,
        CategoryId = null,
    };

    [WolverinePost("/api/v1/guilds/{guildId}/categories")]
    public async Task<IResult> CreateCategory(string guildId, CreateCategoryDto dto,  [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] AuditLogService auditLog,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();


        var category = Category.Create(new CreateCategoryParams()
        {
            Name = dto.Name,
            GuildId = guildId,
            Position = dto.Position,
        });

        ctx.Categories.Add(category);

        auditLog.Log(category.GuildId, userId, AuditActionType.CategoryCreated, category.Id, new { category.Name });

        var presence = await guildHydrateService.GetGuildPresenceAsync(category.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.CategoryCreated", new { CategoryId = category.Id, GuildId = category.GuildId });

        await bus.PublishAsync(ToBotsCreatedEvent(category));

        return Results.Ok(new CategoryDto()
        {
            Name = category.Name,
            Id = category.Id,
            GuildId = category.GuildId,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt,
        });
    }

    [WolverinePatch("/api/v1/categories/{categoryId}")]
    public async Task<IResult> UpdateCategoryAsync(string categoryId, UpdateCategoryDto dto,
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

        var category = await ctx.Categories.FirstOrDefaultAsync(c => c.Id == categoryId);
        if (category is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, category.GuildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        category.Name = dto.Name;

        auditLog.Log(category.GuildId, userId, AuditActionType.CategoryUpdated, categoryId, new { category.Name });

        var presence = await guildHydrateService.GetGuildPresenceAsync(category.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.CategoryUpdated", new { CategoryId = category.Id, GuildId = category.GuildId });

        await bus.PublishAsync(ToBotsUpdatedEvent(category));

        // Built manually rather than via ToFacet<Category, CategoryDto>() - CategoryDto's
        // NestedFacets require the Guild navigation to be loaded (see ThreadEndpoint.
        // GetThreadsAsync's and PermissionOverwriteEndpoint's identical fix), which it never is
        // here.
        return Results.Ok(new CategoryDto
        {
            Name = category.Name,
            Id = category.Id,
            GuildId = category.GuildId,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt,
        });
    }

    [WolverineDelete("/api/v1/categories/{categoryId}")]

    public async Task<IResult> DeleteChannelAsync(string categoryId,
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

        var category = await ctx.Categories.FirstOrDefaultAsync(c => c.Id == categoryId);
        if (category is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, category.GuildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();

        ctx.Categories.Remove(category);

        auditLog.Log(category.GuildId, userId, AuditActionType.CategoryDeleted, categoryId, new { category.Name });

        var presence = await guildHydrateService.GetGuildPresenceAsync(category.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.CategoryDeleted", new { CategoryId = category.Id, GuildId = category.GuildId });

        await bus.PublishAsync(new ChannelDeletedForBots { ChannelId = category.Id, GuildId = category.GuildId });

        return Results.NoContent();
    }

}
