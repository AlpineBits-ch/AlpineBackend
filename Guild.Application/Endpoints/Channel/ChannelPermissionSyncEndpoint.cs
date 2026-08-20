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

        var channel = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.Id, c.GuildId, c.CategoryId })
            .FirstOrDefaultAsync();

        if (channel is null || string.IsNullOrWhiteSpace(channel.CategoryId))
            return (Results.NotFound(), null);

        var guildId = channel.GuildId;

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions))
            return (Results.Forbid(), null);

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return (mfaRejection, null);

        var source = await ctx.Set<ChannelPermission>()
            .AsNoTracking()
            .Where(p => p.CategoryId == channel.CategoryId && p.ChannelId == null)
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

        // SyncFlagAsync re-reads ChannelPermission with AsNoTracking, so it cannot see the rows
        // above until they are actually persisted - the trailing middleware commit is too late for it.
        await ctx.SaveChangesAsync();

        // The flag is a reading of the @everyone overwrite, and the set was just replaced wholesale.
        await channelPrivacy.SyncFlagAsync(channelId);

        auditLog.Log(guildId, userId, AuditActionType.ChannelPermissionChanged, channelId,
            new { ChannelId = channelId, CategoryId = channel.CategoryId, Synced = true });

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
}
