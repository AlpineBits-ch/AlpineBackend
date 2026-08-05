using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wolverine.Http;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Endpoints;

[Authorize]
public class RoleEndpoint()
{
    [WolverinePost("/api/v1/guilds/{guildId}/roles")]
    public async Task<IResult> CreateRoleAsync(string guildId, CreateRoleParams parameters, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles);
        if (!isAuthorized) return Results.Forbid();

        var canGrant = await permissionService.CanGrantPermissionsAsync(userId, guildId, parameters.Permissions);
        if (!canGrant) return Results.Forbid();

        var existingRolePositions = await ctx.Roles
            .Where(r => r.GuildId == guildId)
            .Select(r => r.Position)
            .ToListAsync();
        var maxPosition = existingRolePositions.Count == 0 ? 0 : existingRolePositions.Max();

        var role = Role.Create(new CreateRoleParams()
        {
            Name = parameters.Name,
            Description = parameters.Description,
            GuildId = guildId,
            Color = parameters.Color,
            Type = parameters.Type,
            Permissions = parameters.Permissions,
        });
        role.Position = maxPosition + 1;

        ctx.Roles.Add(role);

        auditLog.Log(guildId, userId, AuditActionType.RoleCreated, role.Id);

        return Results.Ok(role.ToFacet<Role, RoleDto>());
    }

    [WolverinePatch("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleUpdated?)> UpdateRoleAsync(string roleId, UpdateRoleDto roleDto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = Queryable.FirstOrDefault(ctx.Roles, x => x.Id == roleId);

        if(role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        var canGrant = await permissionService.CanGrantPermissionsAsync(userId, role.GuildId, roleDto.Permissions);
        if (!canGrant) return (Results.Forbid(), null);

        role.Permissions = roleDto.Permissions;
        role.Name = roleDto.Name;
        role.Description = roleDto.Description;
        role.Color = roleDto.Color;

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleUpdated, roleId);

        return (Results.Ok(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
        });
    }

    [WolverinePatch("/api/v1/guilds/{guildId}/roles/reorder")]
    public async Task<IResult> ReorderRolesAsync(string guildId, ReorderRolesDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles);
        if (!isAuthorized) return Results.Forbid();

        if (dto.Roles.Count == 0) return Results.NoContent();

        var roleIds = dto.Roles.Select(r => r.RoleId).ToList();
        var roles = await ctx.Roles
            .Where(r => r.GuildId == guildId && roleIds.Contains(r.Id))
            .ToListAsync();

        if (roles.Count != roleIds.Count)
            return Results.BadRequest("One or more roles not found in this guild.");

        // Every role being repositioned must be one the actor already outranks -
        // otherwise a ManagePermissions holder could reorder a role above their own.
        foreach (var roleId in roleIds)
        {
            var canManage = await permissionService.CanManageRoleAsync(userId, guildId, roleId);
            if (!canManage) return Results.Forbid();
        }

        var positionByRoleId = dto.Roles.ToDictionary(r => r.RoleId, r => r.Position);
        foreach (var role in roles)
            role.Position = positionByRoleId[role.Id];

        auditLog.Log(guildId, userId, AuditActionType.RolePositionsChanged);

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.RolesReordered", dto);

        return Results.NoContent();
    }

    [WolverinePut("/api/v1/roles/{roleId}/members/{memberId}")]
    public async Task<(IResult, RoleUpdated?)> AddMemberToRoleAsync(string roleId, string memberId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = ctx.Roles.Include(r => r.Members).FirstOrDefault(x => x.Id == roleId);

        if (role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        // The member must belong to the same guild as the role.
        var memberGuildId = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.Id == memberId)
            .Select(m => m.GuildId)
            .FirstOrDefaultAsync();

        if (memberGuildId is null || memberGuildId != role.GuildId) return (Results.NotFound(), null);

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleUpdated, roleId, new { Action = "MemberAdded", MemberId = memberId });

        var created = DateTime.UtcNow;
        var roleMember = new RoleMember()
        {
            Id = RoleMember.GenerateId(),
            UpdatedAt = created,
            CreatedAt = created,
            RoleId = roleId,
            MemberId = memberId
        };
        
        ctx.RoleMembers.Add(roleMember);

        return (Results.Accepted(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
          MemberId = memberId,
        });
    }
    
    [WolverineDelete("/api/v1/roles/{roleId}/members/{memberId}")]
    public async Task<(IResult, RoleUpdated?)> RemoveMemberFromRoleAsync(string roleId, string memberId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = await ctx.Roles.Include(role => role.Members).FirstOrDefaultAsync(x => x.Id == roleId);

        if (role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        var member = role.Members.FirstOrDefault(x => x.MemberId == memberId);
        if(member == null) return (Results.NotFound(), null);

        role.Members.Remove(member);

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleUpdated, roleId, new { Action = "MemberRemoved", MemberId = memberId });

        return (Results.Accepted(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
          MemberId = memberId,
        });
    }


    [WolverineDelete("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleUpdated?)> DeleteRoleAsync(string roleId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = ctx.Roles.FirstOrDefault(x => x.Id == roleId);

        if(role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        ctx.Roles.Remove(role);

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleDeleted, roleId);

        return (Results.Ok(), new RoleUpdated()
        {
            RoleId  = roleId,
            GuildId = role.GuildId,
            MemberId = null,
        });
    }
}