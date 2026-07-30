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
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => UpsertAsync(ctx, user, permissionService, auditLog, dto, channelId: channelId, roleId: roleId);

    [WolverinePut("/api/v1/channels/{channelId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetChannelMemberOverwriteAsync(
        string channelId, string memberId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => UpsertAsync(ctx, user, permissionService, auditLog, dto, channelId: channelId, memberId: memberId);

    [WolverinePut("/api/v1/categories/{categoryId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetCategoryRoleOverwriteAsync(
        string categoryId, string roleId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => UpsertAsync(ctx, user, permissionService, auditLog, dto, categoryId: categoryId, roleId: roleId);

    [WolverinePut("/api/v1/categories/{categoryId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> SetCategoryMemberOverwriteAsync(
        string categoryId, string memberId, SetPermissionOverwriteDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => UpsertAsync(ctx, user, permissionService, auditLog, dto, categoryId: categoryId, memberId: memberId);

    [WolverineDelete("/api/v1/channels/{channelId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteChannelRoleOverwriteAsync(
        string channelId, string roleId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => RemoveAsync(ctx, user, permissionService, auditLog, channelId: channelId, roleId: roleId);

    [WolverineDelete("/api/v1/channels/{channelId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteChannelMemberOverwriteAsync(
        string channelId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => RemoveAsync(ctx, user, permissionService, auditLog, channelId: channelId, memberId: memberId);

    [WolverineDelete("/api/v1/categories/{categoryId}/permissions/roles/{roleId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteCategoryRoleOverwriteAsync(
        string categoryId, string roleId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => RemoveAsync(ctx, user, permissionService, auditLog, categoryId: categoryId, roleId: roleId);

    [WolverineDelete("/api/v1/categories/{categoryId}/permissions/members/{memberId}")]
    public Task<(IResult, ChannelPermissionChanged?)> DeleteCategoryMemberOverwriteAsync(
        string categoryId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog)
        => RemoveAsync(ctx, user, permissionService, auditLog, categoryId: categoryId, memberId: memberId);

    private static async Task<string?> ResolveGuildIdAsync(MicroserviceContext ctx, string? channelId, string? categoryId)
    {
        if (channelId != null)
            return await ctx.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.GuildId).FirstOrDefaultAsync();

        return await ctx.Categories.AsNoTracking().Where(c => c.Id == categoryId).Select(c => c.GuildId).FirstOrDefaultAsync();
    }

    private static async Task<(IResult, ChannelPermissionChanged?)> UpsertAsync(
        MicroserviceContext ctx, ClaimsPrincipal user, GuildPermissionService permissionService, AuditLogService auditLog,
        SetPermissionOverwriteDto dto, string? channelId = null, string? categoryId = null, string? roleId = null, string? memberId = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var guildId = await ResolveGuildIdAsync(ctx, channelId, categoryId);
        if (guildId is null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);

        var canGrant = await permissionService.CanGrantPermissionsAsync(userId, guildId, dto.AllowPermissions);
        if (!canGrant) return (Results.Forbid(), null);

        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => p.ChannelId == channelId && p.CategoryId == categoryId && p.RoleId == roleId && p.MemberId == memberId)
            .FirstOrDefaultAsync();

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
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Set<ChannelPermission>().Add(overwrite);

        auditLog.Log(guildId, userId, AuditActionType.ChannelPermissionChanged, channelId ?? categoryId,
            new { ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = memberId });

        // NOTE: deliberately not overwrite.ToFacet<ChannelPermission, ChannelPermissionDto>() - the
        // generated mapping unconditionally throws ArgumentNullException when the required nested
        // Channel/Role facets are null (see ChannelPermissionDto's generated constructor), which
        // they always are here since this endpoint never eager-loads those navigations. That threw
        // on every real call except the narrow case where EF's change-tracker fixup happened to
        // already have matching Channel/Role instances tracked in the same DbContext. Built
        // manually instead, same workaround as ThreadEndpoint.GetThreadsAsync uses for the same
        // underlying Facet limitation.
        var overwriteDto = new ChannelPermissionDto
        {
            Id = overwrite.Id,
            ChannelId = overwrite.ChannelId,
            CategoryId = overwrite.CategoryId,
            RoleId = overwrite.RoleId,
            MemberId = overwrite.MemberId,
            AllowPermissions = overwrite.AllowPermissions,
            DenyPermissions = overwrite.DenyPermissions,
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
        string? channelId = null, string? categoryId = null, string? roleId = null, string? memberId = null)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var guildId = await ResolveGuildIdAsync(ctx, channelId, categoryId);
        if (guildId is null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);

        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => p.ChannelId == channelId && p.CategoryId == categoryId && p.RoleId == roleId && p.MemberId == memberId)
            .FirstOrDefaultAsync();

        if (existing is null) return (Results.NotFound(), null);

        ctx.Set<ChannelPermission>().Remove(existing);

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
