using System.Security.Claims;
using System.Text.Json;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Services;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/guilds")]
public class GuildController(MicroserviceContext ctx, GuildThumbnailService thumbnailService, GuildPermissionService permissionService, ILogger<GuildController> logger, ProfileService profileService, GuildHydrateService guildHydrateService, IMessageBus bus) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetGuilds()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        return Ok(ctx.Guilds
            .Include(g => g.Channels.OrderBy(c => c.CreatedAt))
            .Include(g => g.Roles.OrderBy(r => r.Position))
            .Include(g => g.Categories.OrderBy(c => c.CreatedAt))
            .Where(g => g.Members.Any(m => m.UserId == userId)).AsNoTracking()
            .Select(g => g.ToFacet<Domain.Aggregates.Guild, GuildDto>()));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetGuild(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }
        var guild = await ctx.Guilds
            .Include(g => g.Channels.OrderBy(c => c.CreatedAt))
            .Include(g => g.Roles.OrderBy(r => r.Position))
            .Include(g => g.Categories.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(g => g.Id == id);
        if (guild == null)
        {
            return NotFound();
        }
        return Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }


    [HttpGet("{id}/members/search")]
    public async Task<IActionResult> GetGuildMembers(string id, [FromQuery] string search)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId == null) return Unauthorized();


        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ViewChannel))
        {
            return Forbid();
        }
        
        var guildMembers = await ctx.GuildMembers
            .Include(m => m.ReadStates)
            .Where(m => m.GuildId == id)
            .Where(m => m.SearchValue.Contains(search.ToUpperInvariant()))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .ToFacetsAsync<GuildMember, MemberDto>();
        var userIds = guildMembers.Select(m => m.UserId).ToList();
        
        var profiles = await profileService.GetProfilesByUserIds(userIds);
        
        foreach (var member in guildMembers)
        {
            member.Profile = profiles.FirstOrDefault(p => p.UserId == member.UserId);
            if(member.UserId == userId)
            {
                member.ReadStates = [];
            }
        }
        
        return Ok(guildMembers);
    }
    

    [HttpGet("{id}/members")]
    public async Task<IActionResult> GetGuildMembers(string id, [FromQuery] int skip, [FromQuery] int take)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var members = await ctx.GuildMembers
            .Where(m => m.GuildId == id)
            .OrderBy(m => m.CreatedAt)
            .Include(m => m.ReadStates)
            .Take(take).Skip(skip)
            .ToFacetsAsync<GuildMember, MemberDto>();
    
        var presenceMap = await guildHydrateService.GetPresenceByMemberIdsAsync(
            id, 
            members.Select(m => m.Id)
        );
        
        logger.LogInformation("Presence map data: {PresenceMap}", presenceMap);

        foreach (var member in members)
        {
            if (presenceMap.TryGetValue(member.Id, out var presence))
            {
                if (Enum.TryParse<OnlineStatus>(presence.Status, out var status))
                {
                    member.Status = status;
                }
                else
                {
                    logger.LogWarning(
                        "Unrecognized presence status {Status} for member {MemberId}; defaulting to Offline",
                        presence.Status, member.Id);
                    member.Status = OnlineStatus.Offline;
                }
            }
        }
        
        var userIds = members.Select(m => m.UserId).ToList();

        var profiles = await profileService.GetProfilesByUserIds(userIds);
        
        foreach (var member in members)
        {
            member.Profile = profiles.FirstOrDefault(p => p.UserId == member.UserId);
            if(member.UserId == userId)
            {
                member.ReadStates = [];
            }
        }

        logger.LogInformation("Guild members loaded for guild {GuildId} with {Count} members, with {Data}", id, members.Count, JsonSerializer.Serialize(members));
        
        return Ok(members);
    }



    [HttpGet("{guildId}/me")]
    public async Task<IActionResult> GetSelfAsync(string guildId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var member = await ctx.GuildMembers.
            Where(m => m.GuildId == guildId && m.UserId == userId)
            .AsSplitQuery().SingleFacetAsync<GuildMember, SelfMemberDto>();

        return Ok(member);

    }

    [HttpGet("{guildId}/audit-log")]
    public async Task<IActionResult> GetAuditLogAsync(string guildId, [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewAuditLog))
        {
            return Forbid();
        }

        var entries = await ctx.Set<GuildAuditLogEntry>()
            .Where(e => e.GuildId == guildId)
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 100))
            .ToFacetsAsync<GuildAuditLogEntry, AuditLogEntryDto>();

        return Ok(entries);
    }

    [HttpGet("{guildId}/channels")]
    public async Task<IActionResult> GetChannelsAsync(string guildId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var guild = await ctx.Guilds
            .Include(g => g.Channels.OrderBy(c => c.CreatedAt))
            .Include(g => g.Roles.OrderBy(r => r.Position))
            .Include(g => g.Categories.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(g => g.Id == guildId);
        if (guild == null)
        {
            return NotFound();
        }
        var guildDto = guild.ToFacet<Domain.Aggregates.Guild, GuildDto>();        
        return Ok(guildDto.Channels);

    }
    
    
}