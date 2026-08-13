using System.Security.Claims;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Endpoints;

/// <summary>The bit of a player an authorized endpoint needs before it does anything else.</summary>
public sealed record CallerPlayerIdentity(string PlayerId, string SteamId);

/// <summary>Resolves the signed-in account to its player row.</summary>
public static class CallerPlayer
{
    public static async Task<CallerPlayerIdentity?> ResolveAsync(
        ClaimsPrincipal user, MicroserviceContext db, CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var player = await db.Players
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new CallerPlayerIdentity(candidate.Id, candidate.SteamId))
            .FirstOrDefaultAsync(ct);

        return player;
    }
}
