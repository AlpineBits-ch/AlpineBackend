using Microsoft.EntityFrameworkCore;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>The read side of the game catalog.</summary>
public sealed class GameCatalogLookup(MicroserviceContext ctx)
{
    /// <summary>Discord snowflakes are decimal integers.</summary>
    public static bool IsWellFormedApplicationId(string? applicationId) =>
        !string.IsNullOrWhiteSpace(applicationId)
        && applicationId.Length <= 20
        && applicationId.All(char.IsAsciiDigit);

    /// <summary>
    /// The name to broadcast for an application id, or <c>null</c> when the id is malformed,
    /// unknown, or disabled.
    /// </summary>
    public async Task<string?> ResolveCanonicalNameAsync(string? applicationId, CancellationToken ct = default)
    {
        if (!IsWellFormedApplicationId(applicationId)) return null;

        return await ctx.GameApplications
            .AsNoTracking()
            .Where(g => g.DiscordApplicationId == applicationId && g.IsEnabled)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<GameApplication?> FindByApplicationIdAsync(string? applicationId, CancellationToken ct = default)
    {
        if (!IsWellFormedApplicationId(applicationId)) return null;

        return await ctx.GameApplications
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.DiscordApplicationId == applicationId && g.IsEnabled, ct);
    }
}
