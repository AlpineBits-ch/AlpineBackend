using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class MemberEndpoint
{
    /// <summary>Members who joined while onboarding was enabled and never accepted - they can read
    /// but not participate. Gives moderators the "3 people are stuck on the rules screen" view that
    /// was otherwise only derivable one member at a time.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/members/pending")]
    public async Task<IResult> ListPendingMembersAsync(string guildId, int? limit, int? offset,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canModerate = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ModerateMembers)
                          || await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild);
        if (!canModerate) return Results.Forbid();

        var members = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && m.OnboardingCompletedAt == null)
            .OrderBy(m => m.JoinedAt)
            .Skip(Math.Max(offset ?? 0, 0))
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .Select(m => new
            {
                memberId = m.Id,
                userId = m.UserId,
                nickname = m.Nickname,
                joinedAt = m.JoinedAt,
            })
            .ToListAsync();

        return Results.Ok(members);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/bans")]
    public async Task<IResult> BanMemberAsync(string guildId, CreateBanDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.BanMembers);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var canModerate = await permissionService.CanModerateTargetAsync(userId, dto.UserId, guildId);
        if (!canModerate) return Results.Forbid();

        var alreadyBanned = await ctx.Set<GuildBan>().AnyAsync(b => b.GuildId == guildId && b.BannedUserId == dto.UserId);
        if (alreadyBanned) return Results.Conflict("User is already banned.");

        var ban = GuildBan.Create(new CreateGuildBanParams
        {
            GuildId = guildId,
            BannedUserId = dto.UserId,
            BannedByUserId = userId,
            Reason = dto.Reason,
        });
        ctx.Set<GuildBan>().Add(ban);

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == dto.UserId);
        if (member != null) ctx.GuildMembers.Remove(member);

        // ComputePermissionsForUserAsync serves the cached permission set before it ever re-reads
        // membership, so without this the banned user keeps their channel permissions for up to the
        // 15-minute cache TTL while the direct GuildMembers.AnyAsync checks elsewhere already
        // reject them - access that outlives the ban, and an inconsistency that is hard to read.
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, dto.UserId);

        auditLog.Log(guildId, userId, AuditActionType.MemberBanned, dto.UserId, new { dto.Reason });

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        var audience = presence.Select(p => p.UserId).Append(dto.UserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.MemberBanned", new { GuildId = guildId, UserId = dto.UserId, dto.Reason });

        await bus.PublishAsync(new MemberRemovedForBots { GuildId = guildId, UserId = dto.UserId, Reason = "Banned" });

        return Results.Ok(ban.ToFacet<GuildBan, GuildBanDto>());
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/bans/{bannedUserId}")]
    public async Task<IResult> UnbanMemberAsync(string guildId, string bannedUserId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.BanMembers);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var ban = await ctx.Set<GuildBan>().FirstOrDefaultAsync(b => b.GuildId == guildId && b.BannedUserId == bannedUserId);
        if (ban is null) return Results.NotFound();

        // Hierarchy applies to lifting a ban as well as issuing one - see UnmuteMemberAsync.
        if (ban.BannedByUserId != userId &&
            !await permissionService.CanModerateTargetAsync(userId, ban.BannedByUserId, guildId))
        {
            return Results.Forbid();
        }

        ctx.Set<GuildBan>().Remove(ban);

        auditLog.Log(guildId, userId, AuditActionType.MemberUnbanned, bannedUserId);

        return Results.NoContent();
    }

    [WolverineGet("/api/v1/guilds/{guildId}/bans")]
    public async Task<IResult> GetBansAsync(string guildId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.BanMembers);
        if (!isAuthorized) return Results.Forbid();

        var bans = await ctx.Set<GuildBan>()
            .Where(b => b.GuildId == guildId)
            .OrderByDescending(b => b.CreatedAt)
            .ToFacetsAsync<GuildBan, GuildBanDto>();

        return Results.Ok(bans);
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/members/{memberId}")]
    public async Task<IResult> KickMemberAsync(string guildId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.KickMembers);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId);
        if (member is null) return Results.NotFound();

        var canModerate = await permissionService.CanModerateTargetAsync(userId, member.UserId, guildId);
        if (!canModerate) return Results.Forbid();

        ctx.GuildMembers.Remove(member);

        // Same stale-permission-cache reason as the ban path above.
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, member.UserId);

        auditLog.Log(guildId, userId, AuditActionType.MemberKicked, member.UserId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        var audience = presence.Select(p => p.UserId).Append(member.UserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.MemberKicked", new { GuildId = guildId, UserId = member.UserId });

        await bus.PublishAsync(new MemberRemovedForBots { GuildId = guildId, UserId = member.UserId, Reason = "Kicked" });

        return Results.NoContent();
    }

    [WolverinePost("/api/v1/guilds/{guildId}/members/{memberId}/mute")]
    public async Task<IResult> MuteMemberAsync(string guildId, string memberId, MuteMemberDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (dto.DurationMinutes <= 0) return Results.BadRequest("DurationMinutes must be positive.");

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ModerateMembers);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId);
        if (member is null) return Results.NotFound();

        var canModerate = await permissionService.CanModerateTargetAsync(userId, member.UserId, guildId);
        if (!canModerate) return Results.Forbid();

        member.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(dto.DurationMinutes);

        // Mute must take effect immediately, not after the 15-minute permission cache TTL.
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, member.UserId);

        auditLog.Log(guildId, userId, AuditActionType.MemberMuted, member.UserId, new { dto.DurationMinutes });

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        var audience = presence.Select(p => p.UserId).Append(member.UserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.MemberMuted", new { GuildId = guildId, UserId = member.UserId, MutedUntil = member.MutedUntil });

        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = guildId, UserId = member.UserId });

        return Results.NoContent();
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/members/{memberId}/mute")]
    public async Task<IResult> UnmuteMemberAsync(string guildId, string memberId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus, [NotBody] MfaElevationService mfa)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ModerateMembers);
        if (!isAuthorized) return Results.Forbid();

        if (await mfa.RequireAsync(guildId, user) is { } mfaRejection) return mfaRejection;

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId);
        if (member is null) return Results.NotFound();

        // Same hierarchy rule as mute/kick/ban/nickname, which this method was alone in omitting.
        if (!await permissionService.CanModerateTargetAsync(userId, member.UserId, guildId))
            return Results.Forbid();

        member.MutedUntil = null;

        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, member.UserId);

        auditLog.Log(guildId, userId, AuditActionType.MemberUnmuted, member.UserId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        var audience = presence.Select(p => p.UserId).Append(member.UserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.MemberUnmuted", new { GuildId = guildId, UserId = member.UserId });

        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = guildId, UserId = member.UserId });

        return Results.NoContent();
    }

    /// <summary>Maximum nickname length, matching Discord's. Enforced on the trimmed value.</summary>
    private const int MaxNicknameLength = 32;

    [WolverinePatch("/api/v1/guilds/{guildId}/members/me/nickname")]
    public async Task<IResult> UpdateOwnNicknameAsync(string guildId, UpdateNicknameDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ChangeNickname))
            return Results.Forbid();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        return await ApplyNicknameAsync(dto, member, guildId, userId, ctx, auditLog, hub, guildHydrateService, bus);
    }

    [WolverinePatch("/api/v1/guilds/{guildId}/members/{memberId}/nickname")]
    public async Task<IResult> UpdateMemberNicknameAsync(string guildId, string memberId, UpdateNicknameDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId);
        if (member is null) return Results.NotFound();

        // Renaming yourself through the moderator route needs only ChangeNickname - otherwise a
        // member whose client happens to address them by member id would be refused for lacking a
        // permission over other people that they are not exercising.
        if (member.UserId == userId)
        {
            if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ChangeNickname))
                return Results.Forbid();
        }
        else
        {
            if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageNicknames))
                return Results.Forbid();

            // Same hierarchy rule as kick/ban/mute: you cannot rename someone who outranks you,
            // and nobody can rename the owner.
            if (!await permissionService.CanModerateTargetAsync(userId, member.UserId, guildId))
                return Results.Forbid();
        }

        return await ApplyNicknameAsync(dto, member, guildId, userId, ctx, auditLog, hub, guildHydrateService, bus);
    }

    private static async Task<IResult> ApplyNicknameAsync(UpdateNicknameDto dto, GuildMember member,
        string guildId, string actorUserId, MicroserviceContext ctx, AuditLogService auditLog,
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService guildHydrateService, IMessageBus bus)
    {
        var trimmed = dto.Nickname?.Trim();
        var nickname = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (nickname is { Length: > MaxNicknameLength })
            return Results.BadRequest($"Nickname may not exceed {MaxNicknameLength} characters.");

        var previous = member.Nickname;
        if (previous == nickname) return Results.NoContent();

        member.Nickname = nickname;
        // SearchValue is the member-search haystack and must keep tracking the nickname, or a
        // renamed member becomes unfindable by the name everyone actually sees.
        member.SearchValue = GuildMember.BuildSearchValue(member.SearchUsernamePart(), nickname);
        member.UpdatedAt = DateTime.UtcNow;

        auditLog.Log(guildId, actorUserId, AuditActionType.MemberNicknameChanged, member.UserId,
            new { Previous = previous, Current = nickname });

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        var audience = presence.Select(p => p.UserId).Append(member.UserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.MemberUpdated",
            new { GuildId = guildId, UserId = member.UserId, Nickname = nickname });

        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = guildId, UserId = member.UserId });

        return Results.Ok(new { member.UserId, Nickname = nickname });
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/members/me")]
    public async Task<IResult> LeaveGuildAsync(string guildId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] IMessageBus bus,
        [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guild = await ctx.Guilds.FirstOrDefaultAsync(g => g.Id == guildId);
        if (guild is null) return Results.NotFound();

        if (guild.OwnerId == userId)
            return Results.BadRequest("The guild owner cannot leave; delete the guild instead.");

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        ctx.GuildMembers.Remove(member);

        // Same stale-permission-cache reason as the ban path above.
        await permissionService.InvalidateUserPermissionsCacheAsync(guildId, userId);

        auditLog.Log(guildId, userId, AuditActionType.MemberLeft, userId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.MemberLeft", new { GuildId = guildId, UserId = userId });

        await bus.PublishAsync(new MemberRemovedForBots { GuildId = guildId, UserId = userId, Reason = "Left" });

        return Results.NoContent();
    }
}
