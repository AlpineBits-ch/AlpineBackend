using System.Security.Claims;
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

        // Awaited here rather than handing the IQueryable to Ok() and letting serialization
        // enumerate it after this action has already returned. The SQL is unaffected - mapping with
        // `Select(g => g.ToFacet<...>())` is client-evaluated over the materialized entity and keeps
        // all three Includes, verified against the Npgsql generator in
        // GuildListQueryTranslationTests - so this is about *when* the query runs, not what it
        // returns: inside the action, where the DbContext's lifetime and this method's error
        // handling still apply, and consistently with every sibling endpoint here (GetInvitesAsync,
        // GetGuildMembers, GetGuild below), which all materialize before mapping.
        var guilds = await ctx.Guilds
            .AsSplitQuery()
            .Include(g => g.Channels.OrderBy(c => c.CreatedAt))
            .Include(g => g.Roles.OrderBy(r => r.Position))
            .Include(g => g.Categories.OrderBy(c => c.CreatedAt))
            .Where(g => g.Members.Any(m => m.UserId == userId))
            .AsNoTracking()
            .ToListAsync();

        var dtos = guilds.SelectFacets<Domain.Aggregates.Guild, GuildDto>().ToList();

        // Same filter GetGuild applies, per guild: one cached permission read each, then a lookup
        // per channel.
        foreach (var dto in dtos)
            dto.Channels = await VisibleChannelsAsync(userId, dto.Id, dto.Channels);

        return Ok(dtos);
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
        var dto = guild.ToFacet<Domain.Aggregates.Guild, GuildDto>();
        dto.FeatureResolution = GuildFeatureResolutionDto.From(
            await permissionService.GetGuildFeatureResolutionAsync(id));

        dto.Channels = await VisibleChannelsAsync(userId, id, dto.Channels);

        return Ok(dto);
    }

    /// <summary>
    /// Just the module resolution, for a client refetching after an entitlement change.
    /// </summary>
    [HttpGet("{id}/features")]
    public async Task<IActionResult> GetGuildFeatures(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        // Same membership bar as GetGuild.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, id, Permissions.ViewChannel))
        {
            return Forbid();
        }

        return Ok(GuildFeatureResolutionDto.From(
            await permissionService.GetGuildFeatureResolutionAsync(id)));
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
            .Skip(skip).Take(take)
            .ToFacetsAsync<GuildMember, MemberDto>();
    
        var presenceMap = await guildHydrateService.GetPresenceByMemberIdsAsync(
            id, 
            members.Select(m => m.Id)
        );
        
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
        }

        logger.LogInformation("Guild members loaded for guild {GuildId} with {Count} members", id, members.Count);

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

        // Not part of the projection - none of it is a column.
        member.EffectivePermissions = await permissionService.GetGuildPermissionsAsync(userId, guildId);
        member.EffectiveModulePermissions = await permissionService.GetGuildModulePermissionsAsync(userId, guildId);

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
        return Ok(await VisibleChannelsAsync(userId, guildId, guildDto.Channels));

    }

    /// <summary>
    /// Drops the channels this caller may not see. The guild-level gate both callers apply reads
    /// base permissions only, so on its own it hands a member the name and id of every restricted
    /// channel in the guild.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="guildId">The guild the channels belong to.</param>
    /// <param name="channels">Every channel of that guild, already projected.</param>
    /// <returns>The subset holding ViewChannel.</returns>
    private async Task<List<ChannelDto>> VisibleChannelsAsync(
        string userId, string guildId, IEnumerable<ChannelDto> channels)
    {
        var all = channels.ToList();
        if (all.Count == 0) return all;

        // One resolved permission set for the whole page - see
        // GuildPermissionService.FilterChannelsWithPermissionAsync, which reads the caller's cached
        // entry once and answers every channel off it rather than per channel.
        var visible = await permissionService.FilterChannelsWithPermissionAsync(
            userId, guildId, all.Select(c => c.Id).ToList(), Permissions.ViewChannel);

        return all.Where(c => visible.Contains(c.Id)).ToList();
    }
    
    
}