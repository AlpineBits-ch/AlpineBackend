using System.Security.Claims;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Permission;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints.Channel;

/// <summary>
/// Copies a category's overwrites onto one of its channels, replacing whatever the channel had.
/// A one-shot copy, not a stored relationship: nothing records that a channel is "synced", so the
/// client derives that by comparing the two sets it already holds.
/// </summary>
[Authorize]
public class ChannelPermissionSyncEndpoint
{
    [WolverinePost("/api/v1/channels/{channelId}/permissions/sync")]
    public static async Task<(IResult, ChannelPermissionChanged?)> SyncChannelPermissionsAsync(
        string channelId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa,
        [NotBody] ChannelPrivacyService channelPrivacy)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        // Tracked, not projected: SyncFlagFrom below writes IsPrivate onto this same instance and
        // leaves it for the trailing commit rather than saving here.
        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);

        if (channel is null || string.IsNullOrWhiteSpace(channel.CategoryId))
            return (Results.NotFound(), null);

        var guildId = channel.GuildId;
        var categoryId = channel.CategoryId;

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions))
            return (Results.Forbid(), null);

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return (mfaRejection, null);

        var source = await ctx.Set<ChannelPermission>()
            .AsNoTracking()
            .Where(p => p.CategoryId == categoryId && p.ChannelId == null)
            .ToListAsync();

        // Copying a category row must not be a way round the clamp a direct write already has.
        foreach (var row in source)
        {
            if (!await permissionService.CanGrantPermissionsAsync(userId, guildId, row.AllowPermissions) ||
                !await permissionService.CanGrantPermissionsAsync(userId, guildId, row.DenyPermissions) ||
                !await permissionService.CanGrantPermissionsAsync(userId, guildId, row.AllowModulePermissions) ||
                !await permissionService.CanGrantPermissionsAsync(userId, guildId, row.DenyModulePermissions))
                return (Results.Forbid(), null);
        }

        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => p.ChannelId == channelId)
            .ToListAsync();

        // Clearing a row is a grant to whoever it denied, so the hierarchy gate a direct write runs
        // covers both sides of the swap, not just the rows being created.
        foreach (var (targetRoleId, targetMemberId) in TargetsOf(existing, source))
        {
            if (await PermissionOverwriteEndpoint.EnsureTargetIsInGuildAndOutrankedAsync(
                    ctx, permissionService, userId, guildId, targetRoleId, targetMemberId) is not null)
                return (Results.Forbid(), null);
        }

        // Independent of the rows being copied, so this can be read now rather than after the
        // change tracker holds the new set.
        var everyoneRoleId = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId && r.Type == RoleType.Everyone)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        ctx.Set<ChannelPermission>().RemoveRange(existing);

        // Built from the rows just created rather than re-queried, since nothing has been saved yet
        // and the middleware commits after this handler returns.
        var created = new List<ChannelPermission>(source.Count);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in source)
        {
            var overwrite = new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(),
                ChannelId = channelId,
                CategoryId = null,
                RoleId = row.RoleId,
                MemberId = row.MemberId,
                AllowPermissions = row.AllowPermissions,
                DenyPermissions = row.DenyPermissions,
                AllowModulePermissions = row.AllowModulePermissions,
                DenyModulePermissions = row.DenyModulePermissions,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ctx.Set<ChannelPermission>().Add(overwrite);
            created.Add(overwrite);
        }

        // The in-memory form of the flag sync: the copied set replaces the channel's rows wholesale,
        // and nothing has been saved yet for a fresh query to see.
        if (everyoneRoleId is not null)
            channelPrivacy.SyncFlagFrom(channel, created, everyoneRoleId);

        auditLog.Log(guildId, userId, AuditActionType.ChannelPermissionChanged, channelId,
            new { ChannelId = channelId, CategoryId = categoryId, Synced = true });

        var dtos = created.Select(p => new ChannelPermissionDto
        {
            Id = p.Id,
            ChannelId = p.ChannelId,
            CategoryId = p.CategoryId,
            RoleId = p.RoleId,
            MemberId = p.MemberId,
            AllowPermissions = p.AllowPermissions,
            DenyPermissions = p.DenyPermissions,
            AllowModulePermissions = p.AllowModulePermissions,
            DenyModulePermissions = p.DenyModulePermissions,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        }).ToList();

        return (Results.Ok(dtos), new ChannelPermissionChanged { GuildId = guildId });
    }

    /// <summary>Everybody a sync moves: the targets of the rows going away and of the rows arriving.</summary>
    private static IEnumerable<(string? RoleId, string? MemberId)> TargetsOf(
        IEnumerable<ChannelPermission> removed, IEnumerable<ChannelPermission> created) =>
        removed.Concat(created).Select(p => (p.RoleId, p.MemberId)).Distinct();
}
