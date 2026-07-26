using Isle.Api.Services.KingOfTheHill;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class KothStandingDto
{
    public string SteamId { get; init; } = string.Empty;
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
/// Read-only King of the Hill surface for the companion website. Anonymous on purpose: everything here
/// is already broadcast to every player in chat, so there is nothing to protect.
/// </summary>
public static class KingOfTheHillEndpoints
{
    private const string DefinitionDisplayName = "King of the Hill";

    [WolverineGet("/api/v1/koth/active")]
    public static async Task<KothActiveDto?> ActiveMatch(
        [NotBody] KingOfTheHillMatchStateStore stateStore,
        [NotBody] KingOfTheHillControlLedger ledger,
        [NotBody] CancellationToken ct)
    {
        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return null;

        var standings = await ledger.GetStandingsAsync(marker.InstanceId);

        return new KothActiveDto
        {
            InstanceId = marker.InstanceId,
            StartedAt = marker.StartedAt,
            Standings = standings.Select(s => new KothStandingDto { SteamId = s.SteamId, Ticks = s.Ticks }).ToList(),
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
