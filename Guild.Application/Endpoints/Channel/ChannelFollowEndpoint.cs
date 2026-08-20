using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints.Channel;

[Authorize]
public class ChannelFollowEndpoint
{
    /// <summary>Follow direction matches Discord's: initiated from the *target* side - the caller
    /// must be able to see the source (announcement) channel, and must manage channels in
    /// whichever guild owns the target channel receiving the copies.</summary>
    [WolverinePost("/api/v1/channels/{sourceChannelId}/followers")]
    public async Task<IResult> CreateFollow(string sourceChannelId, CreateChannelFollowDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var sourceChannel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == sourceChannelId);
        if (sourceChannel is null) return Results.NotFound("Source channel not found");
        if (sourceChannel.Type != ChannelType.Announcement) return Results.BadRequest("Only Announcement channels can be followed");

        var targetChannel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == dto.TargetChannelId);
        if (targetChannel is null) return Results.NotFound("Target channel not found");

        if (!await permissionService.CanUserPerformActionAsync(userId, sourceChannelId, Permissions.ViewChannel))
            return Results.Forbid();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, targetChannel.GuildId, Permissions.ManageChannel))
            return Results.Forbid();

        var alreadyFollowing = await ctx.Set<GuildChannelFollow>()
            .AnyAsync(f => f.SourceChannelId == sourceChannelId && f.TargetChannelId == dto.TargetChannelId);
        if (alreadyFollowing) return Results.Conflict("This channel is already following that announcement channel");

        var follow = GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = sourceChannelId,
            SourceGuildId = sourceChannel.GuildId,
            TargetChannelId = dto.TargetChannelId,
            TargetGuildId = targetChannel.GuildId,
            CreatedByUserId = userId,
        });

        ctx.Set<GuildChannelFollow>().Add(follow);
        auditLog.Log(targetChannel.GuildId, userId, AuditActionType.ChannelFollowCreated, follow.Id,
            new { SourceChannelId = sourceChannelId, SourceGuildId = sourceChannel.GuildId });

        return Results.Ok(new { follow.Id, follow.SourceChannelId, follow.TargetChannelId });
    }

    [WolverineGet("/api/v1/channels/{sourceChannelId}/followers")]
    public async Task<IResult> ListFollowers(string sourceChannelId, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var sourceChannel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == sourceChannelId);
        if (sourceChannel is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, sourceChannel.GuildId, Permissions.ManageChannel))
            return Results.Forbid();

        var followers = await ctx.Set<GuildChannelFollow>().AsNoTracking()
            .Where(f => f.SourceChannelId == sourceChannelId)
            .Select(f => new { f.Id, f.TargetChannelId, f.TargetGuildId, f.CreatedByUserId, f.CreatedAt })
            .ToListAsync();

        return Results.Ok(followers);
    }

    [WolverineDelete("/api/v1/channels/{sourceChannelId}/followers/{followId}")]
    public async Task<IResult> RemoveFollow(string sourceChannelId, string followId, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var follow = await ctx.Set<GuildChannelFollow>().FirstOrDefaultAsync(f => f.Id == followId && f.SourceChannelId == sourceChannelId);
        if (follow is null) return Results.NotFound();

        // Either side can unfollow: the target guild's managers (most common - "stop receiving
        // these"), or the source guild's managers (revoke someone following you).
        var canManageTarget = await permissionService.CanUserPerformActionOnGuildAsync(userId, follow.TargetGuildId, Permissions.ManageChannel);
        var canManageSource = await permissionService.CanUserPerformActionOnGuildAsync(userId, follow.SourceGuildId, Permissions.ManageChannel);
        if (!canManageTarget && !canManageSource) return Results.Forbid();

        ctx.Set<GuildChannelFollow>().Remove(follow);
        auditLog.Log(canManageTarget ? follow.TargetGuildId : follow.SourceGuildId, userId, AuditActionType.ChannelFollowRemoved, follow.Id);

        return Results.NoContent();
    }
}
