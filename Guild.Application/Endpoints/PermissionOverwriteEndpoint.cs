using System.Security.Claims;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Permission;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class PermissionOverwriteEndpoint
{
    [WolverinePut("/api/v1/channels/{channelId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetChannelRoleOverwriteAsync(
        string channelId, string roleId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => UpsertAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, dto, channelId: channelId, roleId: roleId);

    [WolverinePut("/api/v1/channels/{channelId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetChannelMemberOverwriteAsync(
        string channelId, string memberId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => UpsertAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, dto, channelId: channelId, memberId: memberId);

    [WolverinePut("/api/v1/categories/{categoryId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetCategoryRoleOverwriteAsync(
        string categoryId, string roleId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => UpsertAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, dto, categoryId: categoryId, roleId: roleId);

    [WolverinePut("/api/v1/categories/{categoryId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetCategoryMemberOverwriteAsync(
        string categoryId, string memberId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => UpsertAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, dto, categoryId: categoryId, memberId: memberId);

    [WolverineDelete("/api/v1/channels/{channelId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteChannelRoleOverwriteAsync(
        string channelId, string roleId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => RemoveAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, channelId: channelId, roleId: roleId);

    [WolverineDelete("/api/v1/channels/{channelId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteChannelMemberOverwriteAsync(
        string channelId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => RemoveAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, channelId: channelId, memberId: memberId);

    [WolverineDelete("/api/v1/categories/{categoryId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteCategoryRoleOverwriteAsync(
        string categoryId, string roleId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => RemoveAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, categoryId: categoryId, roleId: roleId);

    [WolverineDelete("/api/v1/categories/{categoryId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteCategoryMemberOverwriteAsync(
        string categoryId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa, [NotBody] ChannelPrivacyService channelPrivacy)
        => RemoveAsync(ctx, user, permissionService, auditLog, mfa, channelPrivacy, categoryId: categoryId, memberId: memberId);

    /// <summary>Validates the overwrite's target.</summary>
    private static async Task<IResult?> EnsureTargetIsInGuildAndOutrankedAsync(
        MicroserviceContext ctx, GuildPermissionService permissionService,
        string userId, string guildId, string? roleId, string? memberId)
    {
        if (roleId is not null)
        {
            var roleInGuild = await ctx.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId && r.GuildId == guildId);
            if (!roleInGuild) return Results.NotFound();

            if (!await permissionService.CanManageRoleAsync(userId, guildId, roleId)) return Results.Forbid();
        }

        if (memberId is not null)
        {
            var targetUserId = await ctx.GuildMembers.AsNoTracking()
                .Where(m => m.Id == memberId && m.GuildId == guildId)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync();

            if (targetUserId is null) return Results.NotFound();

            if (!await permissionService.CanModerateTargetAsync(userId, targetUserId, guildId)) return Results.Forbid();
        }

        return null;
    }

    private static async Task<string?> ResolveGuildIdAsync(MicroserviceContext ctx, string? channelId, string? categoryId)
    {
        if (channelId != null)
            return await ctx.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.GuildId).FirstOrDefaultAsync();

        return await ctx.Categories.AsNoTracking().Where(c => c.Id == categoryId).Select(c => c.GuildId).FirstOrDefaultAsync();
    }

    private static async Task<(IResult, ChannelPermissionChanged?)> UpsertAsync(
        MicroserviceContext ctx, ClaimsPrincipal user, GuildPermissionService permissionService, AuditLogService auditLog,
        MfaElevationService mfa, ChannelPrivacyService channelPrivacy, SetPermissionOverwriteDto dto, string? channelId = null, string? categoryId = null, string? roleId = null, string? memberId = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var guildId = await ResolveGuildIdAsync(ctx, channelId, categoryId);
        if (guildId is null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return (mfaRejection, null);

        // Clamped in BOTH directions.
        var canGrant = await permissionService.CanGrantPermissionsAsync(userId, guildId, dto.AllowPermissions);
        if (!canGrant) return (Results.Forbid(), null);

        var canDeny = await permissionService.CanGrantPermissionsAsync(userId, guildId, dto.DenyPermissions);
        if (!canDeny) return (Results.Forbid(), null);

        // The module masks get the identical treatment, in both directions, against the module
        // overload of the same guard - which already clamps to the guild's enabled GuildFeatures,
        // so a bit belonging to a switched-off module is never grantable by anyone including the
        // owner.
        if (dto.AllowModulePermissions is { } requestedModuleAllow &&
            !await permissionService.CanGrantPermissionsAsync(userId, guildId, requestedModuleAllow))
            return (Results.Forbid(), null);

        if (dto.DenyModulePermissions is { } requestedModuleDeny &&
            !await permissionService.CanGrantPermissionsAsync(userId, guildId, requestedModuleDeny))
            return (Results.Forbid(), null);

        var targetCheck = await EnsureTargetIsInGuildAndOutrankedAsync(ctx, permissionService, userId, guildId, roleId, memberId);
        if (targetCheck is not null) return (targetCheck, null);

        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => p.ChannelId == channelId && p.CategoryId == categoryId && p.RoleId == roleId && p.MemberId == memberId)
            .FirstOrDefaultAsync();

        // Omitted module masks carry over from the row being replaced - see
        // SetPermissionOverwriteDto for why this half of the body is not PUT-replace.
        var allowModulePermissions = dto.AllowModulePermissions ?? existing?.AllowModulePermissions ?? ModulePermissions.None;
        var denyModulePermissions = dto.DenyModulePermissions ?? existing?.DenyModulePermissions ?? ModulePermissions.None;

        // AllowPermissions/DenyPermissions are init-only, so an update is a remove + re-add
        // rather than a mutation of the existing row.
        if (existing != null)
            ctx.Set<ChannelPermission>().Remove(existing);

        var now = DateTimeOffset.UtcNow;
        var overwrite = new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId,
            CategoryId = categoryId,
            RoleId = roleId,
            MemberId = memberId,
            AllowPermissions = dto.AllowPermissions,
            DenyPermissions = dto.DenyPermissions,
            AllowModulePermissions = allowModulePermissions,
            DenyModulePermissions = denyModulePermissions,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Set<ChannelPermission>().Add(overwrite);

        // See RemoveAsync - the flag follows the overwrite, in both directions.
        await channelPrivacy.SyncFlagFromOverwriteAsync(channelId, roleId);

        auditLog.Log(guildId, userId, AuditActionType.ChannelPermissionChanged, channelId ?? categoryId,
            new { ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = memberId });

        // NOTE: deliberately not overwrite.ToFacet<ChannelPermission, ChannelPermissionDto>() - the
        // generated mapping unconditionally throws ArgumentNullException when the required nested
        // Channel/Role facets are null (see ChannelPermissionDto's generated constructor), which
        // they always are here since this endpoint never eager-loads those navigations.
        var overwriteDto = new ChannelPermissionDto
        {
            Id = overwrite.Id,
            ChannelId = overwrite.ChannelId,
            CategoryId = overwrite.CategoryId,
            RoleId = overwrite.RoleId,
            MemberId = overwrite.MemberId,
            AllowPermissions = overwrite.AllowPermissions,
            DenyPermissions = overwrite.DenyPermissions,
            AllowModulePermissions = overwrite.AllowModulePermissions,
            DenyModulePermissions = overwrite.DenyModulePermissions,
            CreatedAt = overwrite.CreatedAt,
            UpdatedAt = overwrite.UpdatedAt,
        };

        return (Results.Ok(overwriteDto), new ChannelPermissionChanged
        {
            GuildId = guildId,
            RoleId = roleId,
            MemberId = memberId,
        });
    }

    private static async Task<(IResult, ChannelPermissionChanged?)> RemoveAsync(
        MicroserviceContext ctx, ClaimsPrincipal user, GuildPermissionService permissionService, AuditLogService auditLog,
        MfaElevationService mfa, ChannelPrivacyService channelPrivacy, string? channelId = null, string? categoryId = null, string? roleId = null, string? memberId = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var guildId = await ResolveGuildIdAsync(ctx, channelId, categoryId);
        if (guildId is null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return (mfaRejection, null);

        // Deleting an overwrite changes the effective permissions of whoever it targets - removing
        // a deny is a grant - so it needs the same hierarchy gate as writing one.
        var targetCheck = await EnsureTargetIsInGuildAndOutrankedAsync(ctx, permissionService, userId, guildId, roleId, memberId);
        if (targetCheck is not null) return (targetCheck, null);

        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => p.ChannelId == channelId && p.CategoryId == categoryId && p.RoleId == roleId && p.MemberId == memberId)
            .FirstOrDefaultAsync();

        if (existing is null) return (Results.NotFound(), null);

        ctx.Set<ChannelPermission>().Remove(existing);

        // Channel.IsPrivate is now a reading of this overwrite rather than a separate claim, so
        // clearing the @everyone ViewChannel deny has to clear the flag with it.
        await channelPrivacy.SyncFlagFromOverwriteAsync(channelId, roleId);

        auditLog.Log(guildId, userId, AuditActionType.ChannelPermissionChanged, channelId ?? categoryId,
            new { ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = memberId, Removed = true });

        return (Results.NoContent(), new ChannelPermissionChanged
        {
            GuildId = guildId,
            RoleId = roleId,
            MemberId = memberId,
        });
    }
}
