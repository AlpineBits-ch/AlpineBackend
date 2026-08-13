using System.Security.Claims;
using Isle.Api.Services.Progression;
using Isle.Infrastructure.Persistence;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class LeaderboardEntryDto
{
    public int Rank { get; init; }

    /// <summary>The in-game name, or a friendly-id fallback.</summary>
    public string Player { get; init; } = string.Empty;

    /// <summary>Their most-played species, or null when no session has been attributed to one yet.</summary>
    public string? Species { get; init; }

    public long Score { get; init; }

    public bool IsCurrentUser { get; init; }
}

public sealed class LeaderboardDto
{
    public IReadOnlyList<LeaderboardEntryDto> Entries { get; init; } = [];

    /// <summary>
    /// The caller's own place, returned even when they are far outside the listed range and even
    /// when they have opted out of appearing in it.
    /// </summary>
    public LeaderboardEntryDto? Self { get; init; }

    /// <summary>False when the caller has opted out, so a page can say why they are not in the list
    /// rather than leaving them to wonder whether the board is broken.</summary>
    public bool SelfIsListed { get; init; }

    /// <summary>How many players the ranking was computed over, so a rank reads as "n of N".</summary>
    public int RankedPlayers { get; init; }
}

/// <summary>The leaderboard.</summary>
public static class LeaderboardEndpoints
{
    [WolverineGet("/api/v1/leaderboard")]
    public static async Task<LeaderboardDto> Get(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] LeaderboardService leaderboard,
        [NotBody] CancellationToken ct,
        int take = LeaderboardService.DefaultTake)
    {
        // Null for an anonymous visitor and equally for a signed-in account with no Steam link.
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);

        var board = await leaderboard.BuildAsync(caller?.PlayerId, take, ct);

        return new LeaderboardDto
        {
            Entries = board.Entries
                .Select(entry => Project(entry, isCurrentUser: entry.PlayerId == caller?.PlayerId))
                .ToList(),
            Self = board.Self is null ? null : Project(board.Self, isCurrentUser: true),
            SelfIsListed = board.Self?.PubliclyListed ?? false,
            RankedPlayers = board.RankedPlayers,
        };
    }

    private static LeaderboardEntryDto Project(LeaderboardRow row, bool isCurrentUser) => new()
    {
        Rank = row.Rank,
        Player = row.PlayerName,
        Species = row.Species,
        Score = row.Score,
        IsCurrentUser = isCurrentUser,
    };
}
