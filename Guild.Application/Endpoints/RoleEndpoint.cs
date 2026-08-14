using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Bus.Events.Role;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Domain.Validators;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Endpoints;

[Authorize]
public class RoleEndpoint()
{
    /// <summary>A single field's before and after, as recorded in the audit metadata.</summary>
    private sealed record RoleFieldChange(string Field, string? Old, string? New);

    private static void Track(List<RoleFieldChange> changes, string field, object? oldValue, object? newValue)
    {
        var before = oldValue?.ToString();
        var after = newValue?.ToString();
        if (before == after) return;

        changes.Add(new RoleFieldChange(field, before, after));
    }

    private static IResult? ValidateShape(Role candidate)
    {
        var result = new RoleValidator().Validate(candidate);
        return result.IsValid ? null : Results.BadRequest(result.Errors[0].ErrorMessage);
    }

    /// <summary>Reads the auth user ids of a role's current holders.</summary>
    private static Task<List<string>> HolderUserIdsAsync(MicroserviceContext ctx, string roleId) =>
        ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => rm.RoleId == roleId)
            .Join(ctx.GuildMembers.AsNoTracking(), rm => rm.MemberId, m => m.Id, (rm, m) => m.UserId)
            .ToListAsync();

    /// <summary>Creates a role, positioned immediately above @everyone.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/roles")]
    public async Task<IResult> CreateRoleAsync(string guildId, CreateRoleDto dto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var canGrant = await permissionService.CanGrantPermissionsAsync(userId, guildId, dto.Permissions);
        if (!canGrant) return Results.Forbid();

        var canGrantModules = await permissionService.CanGrantPermissionsAsync(userId, guildId, dto.ModulePermissions);
        if (!canGrantModules) return Results.Forbid();

        if (!string.IsNullOrWhiteSpace(dto.IconUrl) && !string.IsNullOrWhiteSpace(dto.UnicodeEmoji))
            return Results.BadRequest("A role carries an icon or a unicode emoji, not both.");

        var existingRoles = await ctx.Roles.Where(r => r.GuildId == guildId).ToListAsync();
        if (existingRoles.Count >= RoleValidator.MaxRolesPerGuild)
            return Results.BadRequest($"A guild cannot have more than {RoleValidator.MaxRolesPerGuild} roles.");

        var role = Role.Create(new CreateRoleParams()
        {
            Name = dto.Name,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description,
            GuildId = guildId,
            Color = dto.Color,
            // Never from the body: a second @everyone role is picked up by every singleton lookup
            // for the real one, and its channel overwrites are applied to every member of the
            // guild whether they hold it or not.
            Type = RoleType.None,
            Permissions = dto.Permissions,
            ModulePermissions = dto.ModulePermissions,
            Hoist = dto.Hoist,
            Mentionable = dto.Mentionable,
            IconUrl = dto.IconUrl,
            UnicodeEmoji = dto.UnicodeEmoji,
        });
        role.Position = 1;

        // Validated before anything tracked is touched.
        if (ValidateShape(role) is { } invalid) return invalid;

        foreach (var existing in existingRoles.Where(r => r.Position >= 1))
            existing.Position += 1;

        ctx.Roles.Add(role);

        auditLog.Log(guildId, userId, AuditActionType.RoleCreated, role.Id);

        return Results.Ok(role.ToFacet<Role, RoleDto>());
    }

    /// <summary>The guild's roles, highest first.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/roles")]
    public async Task<IResult> ListGuildRolesAsync(string guildId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // ViewChannel stands in for "is a member of this guild": permission resolution answers None
        // for a non-member, so this is the same gate the rest of the read surface uses.
        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewChannel);
        if (!canView) return Results.Forbid();

        var roles = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .OrderByDescending(r => r.Position)
            .ThenBy(r => r.Id)
            .ToListAsync();

        return Results.Ok(roles.Select(r => r.ToFacet<Role, RoleDto>()).ToList());
    }

    [WolverineGet("/api/v1/roles/{roleId}")]
    public async Task<IResult> GetRoleAsync(string roleId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var role = await ctx.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId);
        if (role == null) return Results.NotFound();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ViewChannel);
        if (!canView) return Results.Forbid();

        return Results.Ok(role.ToFacet<Role, RoleDto>());
    }

    /// <summary>Edits a role's metadata and permissions.</summary>
    [WolverinePatch("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleUpdated?)> UpdateRoleAsync(string roleId, UpdateRoleDto roleDto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = await ctx.Roles.FirstOrDefaultAsync(x => x.Id == roleId);

        if(role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(role.GuildId, user) is { } mfaRejection) return (mfaRejection, null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        if (!role.IsEditableByHumans)
            return (Results.BadRequest("This role is managed by an integration and cannot be edited."), null);

        var newName = roleDto.Name ?? role.Name;
        if (role.Type == RoleType.Everyone && newName != role.Name)
            return (Results.BadRequest($"The @everyone role's name is fixed at \"{Role.EveryoneRoleName}\"."), null);

        if (roleDto.Permissions is { } requestedPermissions)
        {
            var canGrant = await permissionService.CanGrantPermissionsAsync(userId, role.GuildId, requestedPermissions);
            if (!canGrant) return (Results.Forbid(), null);
        }

        if (roleDto.ModulePermissions is { } requestedModulePermissions)
        {
            var canGrantModules = await permissionService.CanGrantPermissionsAsync(userId, role.GuildId, requestedModulePermissions);
            if (!canGrantModules) return (Results.Forbid(), null);
        }

        // The badge is replaced wholesale whenever either half of it is present, rather than
        // merged: the two are mutually exclusive, so "set the icon" can only mean "and drop the
        // emoji". Merging would produce a role holding both, which Role.SetBadge refuses outright.
        var badgeTouched = roleDto.IconUrl != null || roleDto.UnicodeEmoji != null;
        var newIconUrl = badgeTouched ? NullIfBlank(roleDto.IconUrl) : role.IconUrl;
        var newUnicodeEmoji = badgeTouched ? NullIfBlank(roleDto.UnicodeEmoji) : role.UnicodeEmoji;

        if (newIconUrl != null && newUnicodeEmoji != null)
            return (Results.BadRequest("A role carries an icon or a unicode emoji, not both."), null);

        var newDescription = roleDto.Description == null ? role.Description : NullIfBlank(roleDto.Description);
        var newColor = roleDto.Color ?? role.Color;

        // Validated against a detached copy holding the merged result, because the tracked entity
        // is committed by the SaveChanges middleware whether this handler returns Ok or BadRequest.
        var candidate = new Role
        {
            Id = role.Id,
            GuildId = role.GuildId,
            Name = newName,
            Description = newDescription,
            Color = newColor,
            Position = role.Position,
        };
        candidate.SetBadge(newIconUrl, newUnicodeEmoji);

        if (ValidateShape(candidate) is { } invalid) return (invalid, null);

        var changes = new List<RoleFieldChange>();
        Track(changes, nameof(Role.Name), role.Name, newName);
        Track(changes, nameof(Role.Description), role.Description, newDescription);
        Track(changes, nameof(Role.Color), role.Color, newColor);
        Track(changes, nameof(Role.IconUrl), role.IconUrl, newIconUrl);
        Track(changes, nameof(Role.UnicodeEmoji), role.UnicodeEmoji, newUnicodeEmoji);

        if (roleDto.Permissions is { } permissions)
        {
            Track(changes, nameof(Role.Permissions), role.Permissions, permissions);
            role.Permissions = permissions;
        }

        if (roleDto.ModulePermissions is { } modulePermissions)
        {
            Track(changes, nameof(Role.ModulePermissions), role.ModulePermissions, modulePermissions);
            role.ModulePermissions = modulePermissions;
        }

        if (roleDto.Hoist is { } hoist)
        {
            Track(changes, nameof(Role.Hoist), role.Hoist, hoist);
            role.Hoist = hoist;
        }

        if (roleDto.Mentionable is { } mentionable)
        {
            Track(changes, nameof(Role.Mentionable), role.Mentionable, mentionable);
            role.Mentionable = mentionable;
        }

        if (newName != role.Name) role.Rename(newName);
        role.Description = newDescription;
        role.Color = newColor;
        role.SetBadge(newIconUrl, newUnicodeEmoji);

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleUpdated, roleId, new { Changes = changes });

        return (Results.Ok(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
        });
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Moves roles up and down the hierarchy.</summary>
    [WolverinePatch("/api/v1/guilds/{guildId}/roles/reorder")]
    public async Task<IResult> ReorderRolesAsync(string guildId, ReorderRolesDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] MfaElevationService mfa, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        if (dto.Roles.Count == 0) return Results.NoContent();

        var roleIds = dto.Roles.Select(r => r.RoleId).ToList();
        if (roleIds.Distinct().Count() != roleIds.Count)
            return Results.BadRequest("A role can only be given one position.");

        var requestedPositions = dto.Roles.Select(r => r.Position).ToList();
        if (requestedPositions.Any(p => p < 1))
            return Results.BadRequest("Role positions start at 1; position 0 belongs to the @everyone role.");

        if (requestedPositions.Distinct().Count() != requestedPositions.Count)
            return Results.BadRequest("Two roles cannot share a position.");

        var guildRoles = await ctx.Roles.Where(r => r.GuildId == guildId).ToListAsync();
        var roles = guildRoles.Where(r => roleIds.Contains(r.Id)).ToList();

        if (roles.Count != roleIds.Count)
            return Results.BadRequest("One or more roles not found in this guild.");

        if (roles.Any(r => r.Type == RoleType.Everyone))
            return Results.BadRequest("The @everyone role is pinned at position 0 and cannot be moved.");

        if (roles.Any(r => !r.IsEditableByHumans))
            return Results.BadRequest("One or more of these roles is managed by an integration and cannot be moved.");

        // Every role being repositioned must be one the actor already outranks -
        // otherwise a ManagePermissions holder could reorder a role above their own.
        foreach (var roleId in roleIds)
        {
            var canManage = await permissionService.CanManageRoleAsync(userId, guildId, roleId);
            if (!canManage) return Results.Forbid();
        }

        var actorPosition = await permissionService.GetHighestRolePositionAsync(userId, guildId);
        if (actorPosition != int.MaxValue && requestedPositions.Any(p => p >= actorPosition))
            return Results.Forbid();

        var occupiedByOthers = guildRoles
            .Where(r => !roleIds.Contains(r.Id))
            .Select(r => r.Position)
            .ToHashSet();

        if (requestedPositions.Any(occupiedByOthers.Contains))
            return Results.BadRequest("One or more of these positions is held by a role that is not being moved.");

        var positionByRoleId = dto.Roles.ToDictionary(r => r.RoleId, r => r.Position);
        foreach (var role in roles)
            role.Position = positionByRoleId[role.Id];

        auditLog.Log(guildId, userId, AuditActionType.RolePositionsChanged, null,
            new { Changes = roles.Select(r => new { RoleId = r.Id, Position = r.Position }).ToList() });

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.RolesReordered", dto);

        // One message per moved role and nothing per holder: a reorder says which roles changed
        // rank, which is a statement about roles.
        foreach (var role in roles)
            await bus.PublishAsync(new RoleUpdatedForBots
            {
                GuildId = guildId,
                Role = RoleSnapshotMapper.From(role),
            });

        return Results.NoContent();
    }

    /// <summary>Gives a member a role.</summary>
    [WolverinePut("/api/v1/roles/{roleId}/members/{memberId}")]
    public async Task<(IResult, RoleUpdated?)> AddMemberToRoleAsync(string roleId, string memberId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = await ctx.Roles.Include(r => r.Members).FirstOrDefaultAsync(x => x.Id == roleId);

        if (role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(role.GuildId, user) is { } mfaRejection) return (mfaRejection, null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        // The member must belong to the same guild as the role.
        var memberGuildId = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.Id == memberId)
            .Select(m => m.GuildId)
            .FirstOrDefaultAsync();

        if (memberGuildId is null || memberGuildId != role.GuildId) return (Results.NotFound(), null);

        if (role.Members.Any(m => m.MemberId == memberId)) return (Results.NoContent(), null);

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
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = await ctx.Roles.Include(role => role.Members).FirstOrDefaultAsync(x => x.Id == roleId);

        if (role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(role.GuildId, user) is { } mfaRejection) return (mfaRejection, null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        // Membership of @everyone is not optional.
        if (role.Type == RoleType.Everyone)
            return (Results.BadRequest("Every member of the guild holds the @everyone role."), null);

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

    /// <summary>
    /// Replaces a member's whole role set in one call, the way Discord's member patch does.
    /// </summary>
    [WolverinePatch("/api/v1/guilds/{guildId}/members/{memberId}/roles")]
    public async Task<(IResult, MemberRolesUpdated?)> SetMemberRolesAsync(string guildId, string memberId,
        SetMemberRolesDto dto, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return (mfaRejection, null);

        var memberExists = await ctx.GuildMembers
            .AsNoTracking()
            .AnyAsync(m => m.Id == memberId && m.GuildId == guildId);
        if (!memberExists) return (Results.NotFound(), null);

        var requestedIds = dto.RoleIds.Distinct().ToList();
        var guildRoles = await ctx.Roles.AsNoTracking().Where(r => r.GuildId == guildId).ToListAsync();
        var guildRoleIds = guildRoles.Select(r => r.Id).ToHashSet();

        if (requestedIds.Any(id => !guildRoleIds.Contains(id)))
            return (Results.BadRequest("One or more roles not found in this guild."), null);

        var everyoneRoleIds = guildRoles.Where(r => r.Type == RoleType.Everyone).Select(r => r.Id).ToHashSet();

        var desired = requestedIds.Where(id => !everyoneRoleIds.Contains(id)).ToHashSet();

        var currentRows = await ctx.RoleMembers
            .Where(rm => rm.MemberId == memberId && guildRoleIds.Contains(rm.RoleId))
            .ToListAsync();

        var current = currentRows
            .Select(rm => rm.RoleId)
            .Where(id => !everyoneRoleIds.Contains(id))
            .ToHashSet();

        var toAdd = desired.Except(current).ToList();
        var toRemove = current.Except(desired).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return (Results.Ok(currentRows.Select(rm => rm.RoleId).Distinct().ToList()), null);

        foreach (var roleId in toAdd.Concat(toRemove))
        {
            var canManage = await permissionService.CanManageRoleAsync(userId, guildId, roleId);
            if (!canManage) return (Results.Forbid(), null);
        }

        var now = DateTime.UtcNow;
        foreach (var roleId in toAdd)
        {
            ctx.RoleMembers.Add(new RoleMember
            {
                Id = RoleMember.GenerateId(),
                CreatedAt = now,
                UpdatedAt = now,
                RoleId = roleId,
                MemberId = memberId,
            });
        }

        // Every row, not the first match: duplicates predating the idempotent PUT are exactly the
        // case where removing one row leaves the member holding the role.
        var removals = currentRows.Where(rm => toRemove.Contains(rm.RoleId)).ToList();
        ctx.RoleMembers.RemoveRange(removals);

        auditLog.Log(guildId, userId, AuditActionType.RoleUpdated, memberId,
            new { Action = "MemberRolesUpdated", MemberId = memberId, Added = toAdd, Removed = toRemove });

        var resulting = current.Except(toRemove).Concat(toAdd).Concat(everyoneRoleIds).Distinct().ToList();

        return (Results.Ok(resulting), new MemberRolesUpdated
        {
            GuildId = guildId,
            MemberId = memberId,
            AddedRoleIds = toAdd,
            RemovedRoleIds = toRemove,
        });
    }

    /// <summary>Deletes a role.</summary>
    [WolverineDelete("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleDeleted?)> DeleteRoleAsync(string roleId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService, [NotBody] AuditLogService auditLog, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var role = await ctx.Roles.FirstOrDefaultAsync(x => x.Id == roleId);

        if(role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManageRoles);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(role.GuildId, user) is { } mfaRejection) return (mfaRejection, null);

        var canManageRole = await permissionService.CanManageRoleAsync(userId, role.GuildId, roleId);
        if (!canManageRole) return (Results.Forbid(), null);

        // @everyone sits at position 0, so every role above it outranks it and the rank check alone
        // would let a junior moderator delete it.
        if (role.Type == RoleType.Everyone)
            return (Results.BadRequest("The @everyone role cannot be deleted."), null);

        if (!role.IsEditableByHumans)
            return (Results.BadRequest("This role is managed by an integration and cannot be deleted."), null);

        var affectedUserIds = await HolderUserIdsAsync(ctx, roleId);

        ctx.Roles.Remove(role);

        auditLog.Log(role.GuildId, userId, AuditActionType.RoleDeleted, roleId,
            new { role.Name, HolderCount = affectedUserIds.Count });

        return (Results.Ok(), new RoleDeleted()
        {
            RoleId  = roleId,
            GuildId = role.GuildId,
            UserIds = affectedUserIds,
        });
    }
}
