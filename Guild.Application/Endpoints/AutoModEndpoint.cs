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
public class AutoModEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/automod")]
    public async Task<IResult> GetConfig(string guildId, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var config = await ctx.Set<GuildAutoModConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);

        return Results.Ok(new UpdateAutoModConfigDto
        {
            Enabled = config?.Enabled ?? false,
            BlockedWords = config?.BlockedWords ?? [],
            MaxMessagesPerInterval = config?.MaxMessagesPerInterval,
            IntervalSeconds = config?.IntervalSeconds,
        });
    }

    [WolverinePut("/api/v1/guilds/{guildId}/automod")]
    public async Task<IResult> UpdateConfig(string guildId, UpdateAutoModConfigDto dto, [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        if (dto.MaxMessagesPerInterval is <= 0 || dto.IntervalSeconds is <= 0)
            return Results.BadRequest("MaxMessagesPerInterval and IntervalSeconds must be positive when set.");

        var config = await ctx.Set<GuildAutoModConfig>().FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null)
        {
            config = new GuildAutoModConfig { GuildId = guildId };
            ctx.Set<GuildAutoModConfig>().Add(config);
        }

        config.Enabled = dto.Enabled;
        config.BlockedWords = dto.BlockedWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).ToList();
        config.MaxMessagesPerInterval = dto.MaxMessagesPerInterval;
        config.IntervalSeconds = dto.IntervalSeconds;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(guildId, userId, AuditActionType.AutoModConfigUpdated, guildId);

        return Results.Ok(dto);
    }
}
