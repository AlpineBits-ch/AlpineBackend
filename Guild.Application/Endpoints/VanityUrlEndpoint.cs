using System.Security.Claims;
using Echo.Realtime;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>The guild's vanity invite URL: claim it, read it, give it up.</summary>
public class VanityUrlEndpoint
{
    [WolverineGet("/api/v1/guilds/{guildId}/vanity-url")]
    public async Task<IResult> GetVanityUrlAsync(string guildId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService,
        [NotBody] VanityUrlService vanity)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var guild = await ctx.Guilds.AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => new { g.Id, g.VanityUrl, g.VanityUrlSetAt })
            .FirstOrDefaultAsync();

        if (guild is null) return Results.NotFound();

        // Not strict: this is a settings screen, and telling an owner their vanity URL is gone
        // because Billing hiccuped is worse than telling them it is fine while it briefly is not.
        var entitled = await vanity.IsEntitledAsync(guildId, strict: false);

        return Results.Ok(new VanityUrlDto
        {
            GuildId = guild.Id,
            VanityUrl = guild.VanityUrl,
            Entitled = entitled,
            Active = guild.VanityUrl is not null && entitled,
            SetAt = guild.VanityUrlSetAt,
        });
    }

    /// <summary>Claims a slug, or clears the one the guild holds.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/vanity-url")]
    public async Task<IResult> SetVanityUrlAsync(string guildId, SetVanityUrlDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] GuildPermissionService permissionService, [NotBody] AuditLogService auditLog,
        [NotBody] VanityUrlService vanity, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildInviteAudienceService audienceService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Results.Forbid();

        var guild = await ctx.Guilds.FirstOrDefaultAsync(g => g.Id == guildId);
        if (guild is null) return Results.NotFound();

        if (string.IsNullOrWhiteSpace(dto.VanityUrl))
        {
            var released = guild.VanityUrl;
            guild.VanityUrl = null;
            guild.VanityUrlSetAt = null;
            guild.UpdatedAt = DateTimeOffset.UtcNow;

            // The backing invite is left alone deliberately - see Guild.VanityInviteId.
            if (released is not null)
                auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, guildId, new { VanityUrl = (string?)null, Previous = released });

            return Results.Ok(Describe(guild, entitled: await vanity.IsEntitledAsync(guildId, strict: false)));
        }

        var normalized = VanitySlug.Normalize(dto.VanityUrl);
        if (VanitySlug.Validate(normalized) is { } problem) return Results.BadRequest(problem);

        if (string.Equals(guild.VanityUrl, normalized, StringComparison.Ordinal))
            return Results.Ok(Describe(guild, entitled: await vanity.IsEntitledAsync(guildId, strict: false)));

        // Strict: a claim is the persistent artifact, so an unreadable plan denies rather than
        // grants. See VanityUrlService.
        if (!await vanity.IsEntitledAsync(guildId, strict: true))
        {
            return Results.Json(
                new { error = "vanity_url_not_entitled", message = "This guild's plan does not include a vanity URL." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var taken = await ctx.Guilds.AsNoTracking()
            .AnyAsync(g => g.VanityUrl == normalized && g.Id != guildId);
        if (taken) return Results.Conflict("That vanity URL is already taken.");

        guild.VanityUrl = normalized;
        guild.VanityUrlSetAt = DateTimeOffset.UtcNow;
        guild.UpdatedAt = DateTimeOffset.UtcNow;

        await EnsureBackingInviteAsync(guild, userId, ctx, hub, audienceService, bus);

        auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, guildId, new { VanityUrl = normalized });

        return Results.Ok(Describe(guild, entitled: true));
    }

    /// <summary>
    /// Gives the guild a permanent, unlimited invite for its vanity link to point at, if it does
    /// not already have a live one.
    /// </summary>
    private static async Task EnsureBackingInviteAsync(Domain.Aggregates.Guild guild, string userId,
        MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub, GuildInviteAudienceService audienceService, IMessageBus bus)
    {
        if (guild.VanityInviteId is not null)
        {
            var live = await ctx.GuildInvites.AsNoTracking()
                .AnyAsync(i => i.Id == guild.VanityInviteId && i.State != InviteState.Revoked);
            if (live) return;
        }

        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guild.Id,
            Type = InviteType.Permanent,
            InviterId = userId,
        });

        ctx.GuildInvites.Add(invite);
        guild.VanityInviteId = invite.Id;

        await InviteEndpoint.BroadcastCreatedAsync(invite, hub, audienceService, bus);
    }

    private static VanityUrlDto Describe(Domain.Aggregates.Guild guild, bool entitled) => new()
    {
        GuildId = guild.Id,
        VanityUrl = guild.VanityUrl,
        Entitled = entitled,
        Active = guild.VanityUrl is not null && entitled,
        SetAt = guild.VanityUrlSetAt,
    };
}
