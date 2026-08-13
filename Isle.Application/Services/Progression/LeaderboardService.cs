using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Progression;

/// <summary>One place on the board.</summary>
/// <param name="Rank">Competition rank over every player, ties shared. See <see cref="LeaderboardService"/>.</param>
/// <param name="PlayerId">Internal; never serialised outside this service.</param>
/// <param name="PlayerName">The in-game name, falling back to the friendly id. Never a Steam id.</param>
/// <param name="Species">Their most-played species, or null when nothing has been attributed yet.</param>
/// <param name="PubliclyListed">False when the player has opted out of the public listing.</param>
public sealed record LeaderboardRow(
    int Rank,
    string PlayerId,
    string PlayerName,
    string? Species,
    long Score,
    bool PubliclyListed);

/// <summary>The board plus, when the caller has a player, their own place in it.</summary>
/// <param name="Entries">The visible top of the board, longest-standing order.</param>
/// <param name="Self">The caller's own row, present even when they are outside <c>Entries</c> or opted out of it.</param>
/// <param name="RankedPlayers">How many players the ranking was computed over, so a rank can be read as "of N".</param>
public sealed record Leaderboard(IReadOnlyList<LeaderboardRow> Entries, LeaderboardRow? Self, int RankedPlayers);

/// <summary>
/// Ranks players, and is the enforcement point for the leaderboard half of <see
/// cref="PlayerPreferences"/>.
/// </summary>
public sealed class LeaderboardService(MicroserviceContext context, PlaySessionTracker sessions)
{
    /// <summary>Default page size, and the ceiling on what a caller may ask for.</summary>
    public const int DefaultTake = 25;

    public const int MaxTake = 100;

    private sealed record Standing(string PlayerId, string Name, int Sequence, long Score, bool Listed);

    /// <summary>Builds the board.</summary>
    /// <param name="callerPlayerId">The signed-in caller's player id, or null.</param>
    public async Task<Leaderboard> BuildAsync(string? callerPlayerId, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxTake);

        var killWeightByKiller = await context.KillLogs
            .AsNoTracking()
            .Where(log => log.KillerId != null)
            .GroupBy(log => log.KillerId!)
            .Select(group => new { KillerId = group.Key, Weight = group.Sum(log => log.VictimWeightKg) })
            .ToDictionaryAsync(row => row.KillerId, row => row.Weight, StringComparer.Ordinal, ct);

        var players = await context.Players
            .AsNoTracking()
            .Select(player => new
            {
                player.Id,
                player.InGameName,
                player.FriendlyIdSeq,
                player.Xp,

                // No preferences row is the common case and means the defaults, which list the
                // player.
                Listed = player.Preferences == null || player.Preferences.ShowOnLeaderboard,
            })
            .ToListAsync(ct);

        var standings = players
            .Select(player => new Standing(
                player.Id,
                string.IsNullOrWhiteSpace(player.InGameName) ? FallbackName(player.FriendlyIdSeq) : player.InGameName!,
                player.FriendlyIdSeq,
                player.Xp + (long)Math.Round(killWeightByKiller.GetValueOrDefault(player.Id)),
                player.Listed))
            .OrderByDescending(standing => standing.Score)
            .ThenBy(standing => standing.Sequence)
            .ToList();

        var ranks = AssignRanks(standings);

        var visible = standings.Where(standing => standing.Listed).Take(take).ToList();
        var self = callerPlayerId is null
            ? null
            : standings.FirstOrDefault(standing => standing.PlayerId == callerPlayerId);

        // One species lookup for exactly the rows that will be rendered, the caller's included even
        // when they are far down the board.
        var needed = visible.Select(standing => standing.PlayerId).ToList();
        if (self is not null && !needed.Contains(self.PlayerId, StringComparer.Ordinal))
            needed.Add(self.PlayerId);

        var species = await sessions.FavouriteSpeciesAsync(needed, ct);

        return new Leaderboard(
            visible.Select(standing => ToRow(standing, ranks, species)).ToList(),
            self is null ? null : ToRow(self, ranks, species),
            standings.Count);
    }

    /// <summary>
    /// Competition ranking: everyone on the same score shares the first rank that score reaches, and the
    /// next distinct score skips the places they used up.
    /// </summary>
    private static Dictionary<string, int> AssignRanks(IReadOnlyList<Standing> ordered)
    {
        var ranks = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);

        var rank = 0;
        long? previousScore = null;

        for (var index = 0; index < ordered.Count; index++)
        {
            if (previousScore is null || ordered[index].Score != previousScore)
                rank = index + 1;

            previousScore = ordered[index].Score;
            ranks[ordered[index].PlayerId] = rank;
        }

        return ranks;
    }

    private static LeaderboardRow ToRow(
        Standing standing, IReadOnlyDictionary<string, int> ranks, IReadOnlyDictionary<string, string> species) =>
        new(
            ranks.GetValueOrDefault(standing.PlayerId),
            standing.PlayerId,
            standing.Name,
            species.GetValueOrDefault(standing.PlayerId),
            standing.Score,
            standing.Listed);

    /// <summary>What a player with no in-game name is called.</summary>
    private static string FallbackName(int friendlyIdSeq) => $"Player {Player.EncodeFriendlyId(friendlyIdSeq)}";
}
