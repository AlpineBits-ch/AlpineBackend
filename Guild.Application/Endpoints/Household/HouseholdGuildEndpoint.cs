using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints.Household;

/// <summary>The three household modules that aren't channels: home status, quiet hours and
/// time-boxed guest roles. All guild-scoped rather than channel-scoped, so they're gated on their
/// feature flag directly rather than through a channel lookup.</summary>
[Authorize]
public class HouseholdGuildEndpoint
{
    // ── Home status ──────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/guilds/{guildId}/home-status")]
    public async Task<IResult> GetHomeStatusAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] HomeStatusService homeStatus,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        return Results.Ok(await homeStatus.GetAsync(guildId));
    }

    /// <summary>Sets your own status.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/home-status")]
    public async Task<IResult> SetHomeStatusAsync(string guildId, SetHomeStatusDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] HomeStatusService homeStatus,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        if (dto.Note is { Length: > 100 }) return Results.BadRequest("Note must be 100 characters or fewer");

        var status = await homeStatus.SetAsync(guildId, userId, dto.Kind, dto.Note, dto.ExpiresInMinutes);

        await household.BroadcastGuildAsync(guildId, "guild.HomeStatusChanged",
            new { GuildId = guildId, Status = status });

        return Results.Ok(status);
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/home-status")]
    public async Task<IResult> ClearHomeStatusAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] HomeStatusService homeStatus,
        [NotBody] HouseholdChannelService household, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence))
            return Results.Forbid();

        await homeStatus.ClearAsync(guildId, userId);

        await household.BroadcastGuildAsync(guildId, "guild.HomeStatusChanged",
            new { GuildId = guildId, UserId = userId, Cleared = true });

        return Results.NoContent();
    }

    // ── Quiet hours ──────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/guilds/{guildId}/quiet-hours")]
    public async Task<IResult> GetQuietHoursAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.QuietHours))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var config = await ctx.GuildQuietHoursConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);

        // Defaulted rather than 404, same convention as the other guild configs.
        return Results.Ok(new QuietHoursDto
        {
            Enabled = config?.Enabled ?? false,
            StartMinuteLocal = config?.StartMinuteLocal ?? 22 * 60,
            EndMinuteLocal = config?.EndMinuteLocal ?? 7 * 60,
            TimeZoneId = config?.TimeZoneId ?? "UTC",
        });
    }

    [WolverinePut("/api/v1/guilds/{guildId}/quiet-hours")]
    public async Task<IResult> UpdateQuietHoursAsync(string guildId, UpdateQuietHoursDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.QuietHours))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        if (dto.StartMinuteLocal is < 0 or > 1439 || dto.EndMinuteLocal is < 0 or > 1439)
            return Results.BadRequest("Quiet hour minutes must be between 0 and 1439");
        if (dto.StartMinuteLocal == dto.EndMinuteLocal)
            return Results.BadRequest("Quiet hours cannot start and end at the same minute");

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(dto.TimeZoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Results.BadRequest($"'{dto.TimeZoneId}' is not a known IANA time zone");
        }

        var config = await ctx.GuildQuietHoursConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null)
        {
            config = new GuildQuietHoursConfig { GuildId = guildId };
            ctx.GuildQuietHoursConfigs.Add(config);
        }

        config.Enabled = dto.Enabled;
        config.StartMinuteLocal = dto.StartMinuteLocal;
        config.EndMinuteLocal = dto.EndMinuteLocal;
        config.TimeZoneId = dto.TimeZoneId;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, guildId, new { QuietHours = dto.Enabled });
        await ctx.SaveChangesAsync();

        return Results.Ok(new QuietHoursDto
        {
            Enabled = config.Enabled,
            StartMinuteLocal = config.StartMinuteLocal,
            EndMinuteLocal = config.EndMinuteLocal,
            TimeZoneId = config.TimeZoneId,
        });
    }

    // ── Guest access ─────────────────────────────────────────────────────────

    /// <summary>Grants a role that lapses on its own - the pet sitter gets five days of access to
    /// the house manual and the calendar, and nobody has to remember to take it away.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/members/{targetUserId}/roles/{roleId}/temporary")]
    public async Task<IResult> GrantTemporaryRoleAsync(string guildId, string targetUserId, string roleId,
        GrantTemporaryRoleDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.GuestAccess))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageGuests))
            return Results.Forbid();

        if (dto.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest("ExpiresAt must be in the future");
        if (dto.ExpiresAt > DateTimeOffset.UtcNow.AddDays(365))
            return Results.BadRequest("A temporary role may not last longer than a year");

        // The same hierarchy guard the normal role-assignment path uses - a guest grant must not
        // be a way to hand out a role you couldn't assign by hand.
        if (!await permissionService.CanManageRoleAsync(userId, guildId, roleId))
            return Results.Forbid();

        var role = await ctx.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.GuildId == guildId);
        if (role is null) return Results.NotFound();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == targetUserId);
        if (member is null) return Results.NotFound();

        var roleMember = await ctx.RoleMembers.FirstOrDefaultAsync(rm => rm.RoleId == roleId && rm.MemberId == member.Id);
        if (roleMember is null)
        {
            roleMember = new RoleMember
            {
                Id = RoleMember.GenerateId(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RoleId = roleId,
                MemberId = member.Id,
            };
            ctx.RoleMembers.Add(roleMember);
        }

        roleMember.ExpiresAt = dto.ExpiresAt;

        auditLog.Log(guildId, userId, AuditActionType.RoleUpdated, roleId,
            new { TemporaryFor = targetUserId, dto.ExpiresAt });

        await ctx.SaveChangesAsync();

        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, targetUserId);

        // Permission resolution filters expired rows on read, but the resolved set is cached for
        // 15 minutes - so without this the guest keeps their access for up to a quarter of an hour
        // past the expiry. Scheduling the invalidation for the exact instant closes that window.
        await bus.ScheduleAsync(new InvalidateGuestPermissionsCommand
        {
            GuildId = guildId,
            UserId = targetUserId,
        }, dto.ExpiresAt);

        return Results.Ok(new { RoleId = roleId, UserId = targetUserId, roleMember.ExpiresAt });
    }

    // ── Move-out ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes someone who has moved out, and cleans up everything they were carrying.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/members/{targetUserId}/move-out")]
    public async Task<IResult> MoveOutAsync(string guildId, string targetUserId, MoveOutDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MoveOutService moveOut,
        [NotBody] HomeStatusService homeStatus, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] HouseholdChannelService household,
        [NotBody] IMessageBus bus, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == targetUserId);
        if (member is null) return Results.NotFound();

        // The owner is the one member nobody can move out - same rule as kick, and for a household
        // the honest answer is to transfer ownership or delete the guild.
        var ownerId = await ctx.Guilds.AsNoTracking()
            .Where(g => g.Id == guildId).Select(g => g.OwnerId).FirstOrDefaultAsync();
        if (targetUserId == ownerId) return Results.BadRequest("The owner cannot be moved out; transfer ownership first");

        if (!await permissionService.CanModerateTargetAsync(userId, targetUserId, guildId))
            return Results.Forbid();

        if (!dto.WriteOffBalances)
        {
            var outstanding = await moveOut.GetOutstandingBalancesAsync(guildId, targetUserId);
            if (outstanding.Count > 0)
            {
                return Results.Conflict(new
                {
                    Error = "This member is not settled up",
                    Outstanding = outstanding.Select(o => new
                    {
                        o.ChannelId,
                        o.Currency,
                        o.NetMinor,
                    }),
                });
            }
        }

        var summary = await moveOut.StageAsync(guildId, targetUserId, userId, dto.WriteOffBalances);

        ctx.GuildMembers.Remove(member);   // role memberships cascade

        auditLog.Log(guildId, userId, AuditActionType.MemberMovedOut, targetUserId, new
        {
            dto.WriteOffBalances,
            summary.ChoresReassigned,
            summary.ChoresDropped,
            summary.ChoresPaused,
            summary.ListItemsUnassigned,
            WrittenOffMinor = summary.BalancesWrittenOff.Sum(t => t.AmountMinor),
        });

        await ctx.SaveChangesAsync();

        // Same stale-permission-cache reason as the kick path in MemberEndpoint.
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, targetUserId);
        await homeStatus.ClearAsync(guildId, targetUserId);

        await household.BroadcastGuildAsync(guildId, "guild.MemberMovedOut", new
        {
            GuildId = guildId,
            UserId = targetUserId,
            summary.ChoresReassigned,
            summary.ChoresDropped,
            summary.ChoresPaused,
        });

        await bus.PublishAsync(new MemberRemovedForBots
        {
            GuildId = guildId,
            UserId = targetUserId,
            Reason = "MovedOut",
        });

        return Results.Ok(summary);
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/members/{targetUserId}/roles/{roleId}/temporary")]
    public async Task<IResult> RevokeTemporaryRoleAsync(string guildId, string targetUserId, string roleId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.GuestAccess))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageGuests))
            return Results.Forbid();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == targetUserId);
        if (member is null) return Results.NotFound();

        var roleMember = await ctx.RoleMembers
            .FirstOrDefaultAsync(rm => rm.RoleId == roleId && rm.MemberId == member.Id && rm.ExpiresAt != null);
        if (roleMember is null) return Results.NotFound();

        ctx.RoleMembers.Remove(roleMember);
        auditLog.Log(guildId, userId, AuditActionType.RoleUpdated, roleId, new { RevokedFor = targetUserId });

        await ctx.SaveChangesAsync();
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, targetUserId);

        return Results.NoContent();
    }
}

/// <summary>Scheduled for the instant a temporary role lapses - see the grant endpoint.</summary>
public class InvalidateGuestPermissionsCommand
{
    public string GuildId { get; set; } = null!;
    public string UserId { get; set; } = null!;
}

public class InvalidateGuestPermissionsHandler
{
    public static async Task Handle(InvalidateGuestPermissionsCommand command,
        GuildPermissionService permissionService) =>
        await permissionService.InvalidateUserPermissionsCacheAsync(command.GuildId, command.UserId);
}
