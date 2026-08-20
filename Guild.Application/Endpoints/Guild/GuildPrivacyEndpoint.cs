using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>The per-guild DM toggle (privacy spec T2-14).</summary>
[Authorize]
public class GuildPrivacyEndpoint
{
    /// <summary>Every DM override the caller has stored, across every guild.</summary>
    [WolverineGet("/api/v1/users/me/guild-privacy")]
    public static async Task<IResult> GetAllForUserAsync(
        [NotBody] GuildDirectMessagePreferenceService preferences,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var stored = await preferences.GetOverridesAsync(userId);

        return Results.Ok(stored.Select(GuildDirectMessagePreferenceDto.From).ToList());
    }

    /// <summary>Sets the caller's DM toggle for one guild.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/privacy")]
    public static async Task<IResult> UpdateAsync(
        string guildId,
        UpdateGuildPrivacyDto dto,
        [NotBody] GuildDirectMessagePreferenceService preferences,
        // Present so AutoApplyTransactions sees a DbContext on this chain and commits what the
        // service tracked - it keys off the signature, and the service's own injected context is
        // the same scoped instance. Deliberately not used directly here.
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var preference = await preferences.SetAsync(userId, guildId, dto.AllowDirectMessages);
        if (preference is null) return Results.NotFound();

        // No SaveChangesAsync: Wolverine's transactional middleware commits the injected DbContext.
        return Results.Ok(GuildDirectMessagePreferenceDto.From(preference));
    }
}
