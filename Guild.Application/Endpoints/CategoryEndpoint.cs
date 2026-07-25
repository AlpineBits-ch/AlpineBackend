using System.Security.Claims;
using Echo.Realtime;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;

using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

public class CategoryEndpoint
{
    
    [WolverinePost("/api/v1/guilds/{guildId}/categories")]
    public async Task<IResult> CreateCategory(string guildId, CreateCategoryDto dto,  [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, 
        [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService,
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
        var presence = await guildHydrateService.GetGuildPresenceAsync(category.GuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.CategoryCreated", new { CategoryId = category.Id, GuildId = category.GuildId });

        return Results.Ok(new CategoryDto()
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
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        
        var category = await ctx.Categories.FirstOrDefaultAsync(c => c.Id == categoryId);
        if (category is null) return Results.NotFound();
        
        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, category.GuildId, Permissions.ManageChannel);
        if (!canManage) return Results.Forbid();
        
        ctx.Categories.Remove(category);
        
        var presence = await guildHydrateService.GetGuildPresenceAsync(category.GuildId);
        
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.CategoryDeleted", new { CategoryId = category.Id, GuildId = category.GuildId });
        
        return Results.NoContent();
    }

}