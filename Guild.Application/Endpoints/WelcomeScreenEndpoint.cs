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

/// <summary>The invite splash - what someone sees while deciding whether to join.</summary>
[Authorize]
public class WelcomeScreenEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/welcome-screen")]
    public async Task<IResult> Get(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var isMember = await ctx.GuildMembers.AsNoTracking().AnyAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (!isMember) return Results.NotFound();

        return Results.Ok(await LoadAsync(ctx, guildId));
    }

    [WolverinePut("/api/v1/guilds/{guildId}/welcome-screen")]
    public async Task<IResult> Update(string guildId, UpdateWelcomeScreenDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var error = await ValidateAsync(guildId, dto, ctx);
        if (error is not null) return Results.BadRequest(error);

        var screen = await ctx.Set<GuildWelcomeScreen>()
            .Include(s => s.Channels)
            .FirstOrDefaultAsync(s => s.GuildId == guildId);

        if (screen is null)
        {
            screen = new GuildWelcomeScreen { GuildId = guildId };
            ctx.Set<GuildWelcomeScreen>().Add(screen);
        }

        screen.Enabled = dto.Enabled;
        screen.Description = dto.Description;
        screen.UpdatedAt = DateTimeOffset.UtcNow;

        // Small, ordered, and fully client-owned - replacing the rows outright is simpler than
        // reconciling them, and nothing references a welcome channel by id.
        ctx.Set<GuildWelcomeChannel>().RemoveRange(screen.Channels);

        var now = DateTimeOffset.UtcNow;
        screen.Channels = dto.Channels.Select((c, index) => new GuildWelcomeChannel
        {
            Id = GuildWelcomeChannel.GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            GuildId = guildId,
            ChannelId = c.ChannelId,
            Description = c.Description,
            Emoji = c.Emoji,
            Position = index,
        }).ToList();

        auditLog.Log(guildId, userId, AuditActionType.WelcomeScreenUpdated, guildId, new
        {
            dto.Enabled,
            ChannelCount = screen.Channels.Count,
        });

        return Results.Ok(ToDto(screen));
    }

    /// <summary>Shared with the invite preview so a pre-join client sees the same payload.</summary>
    internal static async Task<UpdateWelcomeScreenDto?> LoadAsync(MicroserviceContext ctx, string guildId,
        bool enabledOnly = false)
    {
        var screen = await ctx.Set<GuildWelcomeScreen>()
            .AsNoTracking()
            .Include(s => s.Channels)
            .FirstOrDefaultAsync(s => s.GuildId == guildId);

        if (screen is null) return enabledOnly ? null : new UpdateWelcomeScreenDto();
        if (enabledOnly && !screen.Enabled) return null;

        return ToDto(screen);
    }

    private static UpdateWelcomeScreenDto ToDto(GuildWelcomeScreen screen) => new()
    {
        Enabled = screen.Enabled,
        Description = screen.Description,
        Channels = screen.Channels.OrderBy(c => c.Position).Select(c => new WelcomeChannelDto
        {
            ChannelId = c.ChannelId,
            Description = c.Description,
            Emoji = c.Emoji,
            Position = c.Position,
        }).ToList(),
    };

    private static async Task<string?> ValidateAsync(string guildId, UpdateWelcomeScreenDto dto, MicroserviceContext ctx)
    {
        if (dto.Description is { Length: > GuildWelcomeScreen.MaxDescriptionLength })
            return $"Description may not exceed {GuildWelcomeScreen.MaxDescriptionLength} characters.";

        if (dto.Channels.Count > GuildWelcomeScreen.MaxChannels)
            return $"At most {GuildWelcomeScreen.MaxChannels} welcome channels may be configured.";

        if (dto.Channels.Select(c => c.ChannelId).Distinct().Count() != dto.Channels.Count)
            return "The same channel may not appear twice.";

        foreach (var channel in dto.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Description))
                return "Every welcome channel needs a description.";

            if (channel.Description.Length > GuildWelcomeScreen.MaxChannelDescriptionLength)
                return $"Welcome channel descriptions may not exceed {GuildWelcomeScreen.MaxChannelDescriptionLength} characters.";
        }

        var channelIds = dto.Channels.Select(c => c.ChannelId).ToList();
        if (channelIds.Count == 0) return null;

        var known = await ctx.Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId && channelIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();

        var unknown = channelIds.Except(known).ToList();
        return unknown.Count > 0
            ? $"Unknown channel(s) for this guild: {string.Join(", ", unknown)}."
            : null;
    }
}
