using System.Security.Claims;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The gate behind <see cref="Domain.Aggregates.Guild.MfaRequired"/>: a guild may demand that every
/// moderation and permission-changing action come from a session that actually presented a second
/// factor.
/// </summary>
public class MfaElevationService(MicroserviceContext ctx)
{
    /// <summary>OpenID Connect's authentication-methods-references claim.</summary>
    public const string AmrClaimType = "amr";

    /// <summary>The <c>amr</c> value Identity writes for a session that cleared a second factor.</summary>
    public const string MfaMethod = "mfa";

    public const string UserTypeClaimType = "user_type";
    public const string BotUserType = "Bot";

    public static bool HasMfa(ClaimsPrincipal user) =>
        user.FindAll(AmrClaimType).Any(claim => string.Equals(claim.Value, MfaMethod, StringComparison.Ordinal));

    public static bool IsBot(ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue(UserTypeClaimType), BotUserType, StringComparison.OrdinalIgnoreCase);

    public Task<bool> IsRequiredAsync(string guildId) =>
        ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.MfaRequired)
            .FirstOrDefaultAsync();

    /// <summary>Whether <paramref name="user"/> may perform a gated action in this guild.</summary>
    public async Task<bool> IsSatisfiedAsync(string guildId, ClaimsPrincipal user)
    {
        if (HasMfa(user) || IsBot(user)) return true;

        return !await IsRequiredAsync(guildId);
    }

    /// <summary>Null when the caller may proceed, otherwise the response to return.</summary>
    public async Task<IResult?> RequireAsync(string guildId, ClaimsPrincipal user) =>
        await IsSatisfiedAsync(guildId, user) ? null : Rejection();

    /// <summary>403 with a discriminator, rather than a bare <c>Results.Forbid()</c>.</summary>
    public static IResult Rejection() =>
        Results.Json(Body, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The MVC-controller twin of <see cref="Rejection"/>, for the asset controllers, which
    /// are not Wolverine handlers and so speak <see cref="IActionResult"/>.</summary>
    public static ObjectResult RejectionResult() =>
        new(Body) { StatusCode = StatusCodes.Status403Forbidden };

    private static object Body => new
    {
        error = "mfaRequired",
        action = "enrollMfa",
        message = "This guild requires two-factor authentication for moderation and permission changes.",
    };
}
