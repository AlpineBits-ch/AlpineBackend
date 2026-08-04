using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The per-guild DM toggle (privacy spec T2-14).
///
/// <para>Shaped after <see cref="NotificationSettingEndpoint"/>, which solves the same problem: a
/// per-user, per-guild preference with a bulk-hydration route so a cold client does not issue one
/// request per guild. As there, every route acts on the caller and only the caller - no permission
/// could ever let one member set another's, so there is no permission check beyond membership.</para>
/// </summary>
[Authorize]
public class GuildPrivacyEndpoint
{
    /// <summary>
    /// Every DM override the caller has stored, across every guild.
    ///
    /// <para>Overrides only, not the effective value for every guild the caller is in: the client
    /// already knows the account-level policy from <c>GET /api/v1/privacy-settings</c>, and
    /// returning a synthesized row per guild would make an inherited value indistinguishable from
    /// an explicitly chosen one. <see cref="GuildDirectMessagePreferenceDto.IsOverride"/> is
    /// therefore always true here.</para>
    /// </summary>
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

    /// <summary>
    /// Sets the caller's DM toggle for one guild.
    ///
    /// <para>404 when the caller is not a member - not 403. There is nothing to consent to in a
    /// server you are not in, and answering differently for "guild exists, you are not in it" and
    /// "no such guild" would turn this into a guild-enumeration oracle.</para>
    /// </summary>
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
