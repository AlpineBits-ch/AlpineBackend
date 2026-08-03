using Isle.Api.Services.KingOfTheHill;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class KothStandingDto
{
    /// <summary>
    /// The player's in-game name, falling back to "Unknown player" when it cannot be resolved.
    ///
    /// Deliberately not the Steam ID. This endpoint is anonymous, and a Steam ID resolves to a
    /// public Steam profile - so returning the standings by Steam ID turned it into an
    /// unauthenticated, scriptable "who is on this server right now" feed. The in-game name is what
    /// the player-facing <c>!koth</c> command shows; the Steam-ID list is admin-gated behind
    /// <c>!kothadmin status</c>.
    /// </summary>
    public string PlayerName { get; init; } = string.Empty;
    public int Ticks { get; init; }
}

public sealed class KothActiveDto
{
    public string InstanceId { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public IReadOnlyList<KothStandingDto> Standings { get; init; } = [];
}

public sealed class KothRunDto
{
    public string InstanceId { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime EndedAt { get; init; }
    public IReadOnlyList<ParticipantResult> Results { get; init; } = [];
}

/// <summary>
/// Read-only King of the Hill surface for the companion website. Anonymous on purpose - but only
/// for what players already see in chat. Identifiers that the in-game surface does not expose to
/// ordinary players (Steam IDs) are resolved to display names before leaving this boundary.
/// </summary>
public static class KingOfTheHillEndpoints
{
    private const string DefinitionDisplayName = "King of the Hill";

    [WolverineGet("/api/v1/koth/active")]
    public static async Task<KothActiveDto?> ActiveMatch(
        [NotBody] KingOfTheHillMatchStateStore stateStore,
        [NotBody] KingOfTheHillControlLedger ledger,
        [NotBody] MicroserviceContext db,
        [NotBody] CancellationToken ct)
    {
        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return null;

        var standings = await ledger.GetStandingsAsync(marker.InstanceId);

        // One batched lookup rather than a query per standing - same resolution KothCommand does
        // for the in-game listing.
        var steamIds = standings.Select(s => s.SteamId).Distinct().ToList();
        var namesBySteamId = await db.Players
            .AsNoTracking()
            .Where(p => steamIds.Contains(p.SteamId))
            .ToDictionaryAsync(p => p.SteamId, p => p.InGameName, ct);

        return new KothActiveDto
        {
            InstanceId = marker.InstanceId,
            StartedAt = marker.StartedAt,
            Standings = standings.Select(s => new KothStandingDto
            {
                PlayerName = namesBySteamId.TryGetValue(s.SteamId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : "Unknown player",
                Ticks = s.Ticks,
            }).ToList(),
        };
    }

    [WolverineGet("/api/v1/koth/history")]
    public static async Task<IReadOnlyList<KothRunDto>> History(
        [NotBody] MicroserviceContext db, [NotBody] CancellationToken ct)
    {
        var runs = await db.GameModeRuns
            .AsNoTracking()
            .Where(r => r.Definition != null && r.Definition.DisplayName == DefinitionDisplayName)
            .OrderByDescending(r => r.EndedAt)
            .Take(20)
            .ToListAsync(ct);

        return runs.Select(r => new KothRunDto
        {
            InstanceId = r.Id,
            StartedAt = r.StartedAt,
            EndedAt = r.EndedAt,
            Results = r.Results,
        }).ToList();
    }
}
