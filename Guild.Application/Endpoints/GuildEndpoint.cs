using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence.Durability;

namespace Guild.Application.Endpoints;

[Authorize]
public class GuildEndpoint
{
    [WolverinePost("/api/v1/guilds")]
    public async Task<IResult> CreateGuild(CreateGuildDto dto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {

        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!
        });

        if(profileResponse.Profile is null) return Results.BadRequest("User not found");
        
        var searchValue = profileResponse.Profile.UserName! + "#" + profileResponse.Profile.Hash;
        
        var guild = Domain.Aggregates.Guild.Create(new CreateGuildParams()
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            OwnerSearchValue = searchValue.ToUpperInvariant(),
            OwnerNickname = profileResponse.Profile.UserName,
        });
        
        ctx.Guilds.Add(guild);
        
        
        // because of the sys channel we have to do some hacks 

        var sysChannelId = guild.SystemChannelId;
        guild.SystemChannelId = null;
        await ctx.SaveChangesAsync();
        guild.SystemChannelId = sysChannelId;
        
        
        return Results.Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }
    
    
    
    [WolverineDelete("/api/v1/guilds/{id}")]
    public async Task<IResult> DeleteGuild(string id, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await ctx.Guilds.FirstOrDefaultAsync(g => g.Id == id);
        if (guild is null) return Results.NotFound();

        // Only the owner may delete the guild — matches Discord (no delegated "ManageGuild"
        // holder can do this, since it's irreversible and destroys everyone's data).
        if (guild.OwnerId != userId) return Results.Forbid();

        var memberUserIds = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == id)
            .Select(m => m.UserId)
            .ToListAsync();

        ctx.Guilds.Remove(guild);
        await ctx.SaveChangesAsync();

        // Resolved from the DB (not presence), so any client currently connected —
        // even idle/backgrounded — gets the eviction event immediately.
        await hub.Clients.Users(memberUserIds).SendAsync("guild.GuildDeleted", new { GuildId = id });

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/guilds/{id}")]
    public async Task<IResult> UpdateGuild(string id, UpdateGuildDto dto, [NotBody] MicroserviceContext context,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await context.Guilds.Include(g => g.Channels)
            .Include(g => g.Categories)
            .ThenInclude(c => c.Channels)
            .Where(g => g.Id == id).FirstOrDefaultAsync();
        if (guild is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ManageGuild);
        if (!canManage) return Results.Forbid();

        guild.Name = dto.Name;
        guild.Description = dto.Description;

        if (dto.SystemChannelId is not null)
        {
            var channel = guild.Channels.FirstOrDefault(c => c.Id == dto.SystemChannelId);
            if (channel is null || (channel.Type != ChannelType.Text && channel.Type != ChannelType.Announcement))
                return Results.BadRequest("System channel must be a text or announcement channel in this guild");

            guild.SystemChannelId = dto.SystemChannelId;
        }

        auditLog.Log(id, userId, AuditActionType.GuildUpdated, id);

        var presence = await guildHydrateService.GetGuildPresenceAsync(id);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.GuildUpdated", new { GuildId = id });

        return Results.Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }

}