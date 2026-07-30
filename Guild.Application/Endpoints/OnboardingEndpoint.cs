using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class OnboardingEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/onboarding")]
    public async Task<IResult> GetConfig(string guildId, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var config = await ctx.Set<GuildOnboardingConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);

        return Results.Ok(new UpdateOnboardingConfigDto
        {
            Enabled = config?.Enabled ?? false,
            RulesText = config?.RulesText,
            DefaultChannelIds = config?.DefaultChannelIds ?? [],
        });
    }

    [WolverinePut("/api/v1/guilds/{guildId}/onboarding")]
    public async Task<IResult> UpdateConfig(string guildId, UpdateOnboardingConfigDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        if (dto.Enabled && string.IsNullOrWhiteSpace(dto.RulesText))
            return Results.BadRequest("RulesText is required to enable onboarding.");

        var config = await ctx.Set<GuildOnboardingConfig>().FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null)
        {
            config = new GuildOnboardingConfig { GuildId = guildId };
            ctx.Set<GuildOnboardingConfig>().Add(config);
        }

        config.Enabled = dto.Enabled;
        config.RulesText = dto.RulesText;
        config.DefaultChannelIds = dto.DefaultChannelIds;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(guildId, userId, AuditActionType.OnboardingConfigUpdated, guildId);

        return Results.Ok(dto);
    }

    /// <summary>What a member sees before accepting - rules text, default channels, and whether
    /// they've already accepted (so the client knows whether to show this screen at all).</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/onboarding/me")]
    public async Task<IResult> GetMyStatus(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        var config = await ctx.Set<GuildOnboardingConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);

        return Results.Ok(new
        {
            completed = member.OnboardingCompletedAt is not null,
            rulesText = config?.RulesText,
            defaultChannelIds = config?.DefaultChannelIds ?? [],
        });
    }

    [WolverinePost("/api/v1/guilds/{guildId}/onboarding/accept")]
    public async Task<IResult> Accept(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return Results.NotFound();

        if (member.OnboardingCompletedAt is null)
        {
            member.OnboardingCompletedAt = DateTimeOffset.UtcNow;
            await permissionService.InvalidateUserPermissionsCacheAsync(guildId, userId);
        }

        return Results.Ok();
    }
}
