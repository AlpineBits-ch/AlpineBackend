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
public class GuildController(MicroserviceContext ctx, GuildThumbnailService thumbnailService, GuildPermissionService permissionService, ILogger<GuildController> logger, ProfileService profileService, GuildHydrateService guildHydrateService, IMessageBus bus, PrivacySettingsCache privacySettings) : ControllerBase
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
        // Membership is required.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ViewChannel))
        {
            return Forbid();
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

        // Same gate its sibling .../members/search already applies.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ViewChannel))
        {
            return Forbid();
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

        // One privacy read for the whole page rather than one per member: ShareActivity gates the
        // activity half of every row below, and the cache is Redis-backed.
        var memberUserIds = members.Select(m => m.UserId).Distinct(StringComparer.Ordinal).ToList();
        var privacyByUserId = await privacySettings.GetAsync(memberUserIds);

        foreach (var member in members)
        {
            if (presenceMap.TryGetValue(member.Id, out var presence))
            {
                var viewerIsSubject = member.UserId == userId;

                if (PresenceProjection.TryParse(presence.Status, out var status))
                {
                    // Projected, not assigned raw.
                    member.Status = PresenceProjection.ProjectFor(status, viewerIsSubject);
                }
                else
                {
                    logger.LogWarning(
                        "Unrecognized presence status {Status} for member {MemberId}; defaulting to Offline",
                        presence.Status, member.Id);
                    member.Status = OnlineStatus.Offline;
                    status = OnlineStatus.Offline;
                }

                // Projected against the parsed stored status, not member.Status, which has already
                // been flattened - the Hidden gate needs to see the truth to act on it.
                var hasPrivacy = privacyByUserId.TryGetValue(member.UserId, out var memberPrivacy);

                member.Activities = PresenceProjection.ProjectActivitiesFor(
                    presence.Activities,
                    status,
                    viewerIsSubject,
                    hasPrivacy && memberPrivacy!.ShareActivity,
                    memberPrivacy?.HiddenActivities);
            }
        }
        
        var userIds = members.Select(m => m.UserId).ToList();

        var profiles = await profileService.GetProfilesByUserIds(userIds);

        var memberIds = members.Select(m => m.Id).ToList();
        var roleAssignmentsByMember = (await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => memberIds.Contains(rm.MemberId))
                .Select(rm => new { rm.MemberId, Role = rm.Role.ToFacet<Role, RoleDto>() })
                .ToListAsync())
            .ToLookup(x => x.MemberId);

        foreach (var member in members)
        {
            member.Profile = profiles.FirstOrDefault(p => p.UserId == member.UserId);
            member.RoleMembers = roleAssignmentsByMember[member.Id]
                .Select(x => new MemberRoleAssignmentDto { Role = x.Role })
                .ToList();
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

        // First, not Single: SingleAsync throws - and so 500s - both when the caller isn't a member
        // of this guild and if a duplicate membership row ever exists. Neither is a server fault.
        var member = await ctx.GuildMembers
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .AsSplitQuery().FirstFacetAsync<GuildMember, SelfMemberDto>();

        if (member is null) return NotFound();

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

        // Same membership requirement as GetGuild - this is the same private channel list.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewChannel))
        {
            return Forbid();
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