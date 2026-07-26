using Isle.Api.Services.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Interfaces;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Games.KingOfTheHill;

/// <summary>
/// King of the Hill's rules: whoever racked up the most control ticks in
/// <see cref="KingOfTheHillControlLedger"/> wins. The lifecycle hooks are thin — the actual tick-by-tick
/// accounting is driven by <see cref="KingOfTheHillTickService"/>, which is what calls
/// <see cref="OnTickAsync"/> — because that is where the per-tick zone/roster work already lives; this
/// class only has to answer "who's ahead" once, at resolution.
/// </summary>
public class KingOfTheHillMode(
    KingOfTheHillControlLedger ledger,
    MicroserviceContext context,
    ILogger<KingOfTheHillMode> logger) : IGameMode
{
    public Task OnStartAsync(GameModeInstance instance) => Task.CompletedTask;

    public Task OnTickAsync(GameModeInstance instance, TimeSpan elapsed) => Task.CompletedTask;

    public Task OnEndAsync(GameModeInstance instance) => Task.CompletedTask;

    /// <summary>
    /// Ranks every player credited a control tick, most ticks first. Called once, at match resolution,
    /// from a background-service scope with no captured synchronization context, so blocking on the
    /// ledger's async read here is safe — simpler than threading an async ranking path through
    /// <see cref="IGameMode"/> for its one caller.
    /// </summary>
    public IReadOnlyList<ParticipantStanding> GetStandings(GameModeInstance instance)
    {
        var standings = ledger.GetStandingsAsync(instance.InstanceId).GetAwaiter().GetResult();
        if (standings.Count == 0)
            return [];

        var steamIds = standings.Select(s => s.SteamId).ToList();

        // Grouped rather than keyed straight into a dictionary: nothing constrains steam_id to be
        // unique, and a duplicate would throw here after the match has already been resolved.
        var bySteam = context.Players
            .AsNoTracking()
            .Where(p => steamIds.Contains(p.SteamId))
            .ToList()
            .GroupBy(p => p.SteamId)
            .ToDictionary(g => g.Key, g => g.First());

        var ranked = new List<ParticipantStanding>();
        var placing = 0;

        foreach (var standing in standings)
        {
            if (!bySteam.TryGetValue(standing.SteamId, out var player))
            {
                logger.LogDebug("KOTH participant {Steam} is not a registered player; excluded from standings",
                    standing.SteamId);
                continue;
            }

            placing++;
            ranked.Add(new ParticipantStanding
            {
                PlayerId = player.Id,
                Score = standing.Ticks,
                Rank = placing,
                CustomMetrics = new Dictionary<string, object>(),
            });
        }

        return ranked;
    }

    /// <summary>No mode-specific bonus rows for v1 — <c>Definition.Rewards</c> is the whole reward table.</summary>
    public IReadOnlyList<RewardConfig> GetRewards(GameModeInstance instance, ParticipantStanding standing) => [];
}
