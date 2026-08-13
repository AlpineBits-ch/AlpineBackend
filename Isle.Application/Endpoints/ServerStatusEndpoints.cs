using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

public sealed class ServerStatusDto
{
    /// <summary>How many players the last RCON roster read saw in the world.</summary>
    public int OnlinePlayerCount { get; init; }

    /// <summary>
    /// How many players with a Venta-linked account this service currently believes are in-game.
    /// </summary>
    public int LinkedPlayersInGame { get; init; }

    /// <summary>When the roster snapshot behind these numbers was taken, or null if none ever landed.</summary>
    public DateTimeOffset? RosterLastUpdatedAt { get; init; }

    /// <summary>
    /// True when the snapshot is older than <see cref="RosterStalenessThresholdSeconds"/>, which in
    /// practice means RCON is unreachable or the dedicated server is down.
    /// </summary>
    public bool RosterIsStale { get; init; }

    public int RosterStalenessThresholdSeconds { get; init; }
}

/// <summary>One player as the last roster read saw them.</summary>
public sealed class ServerRosterEntryDto
{
    /// <summary>The in-game name, or "Unknown player" when the game server reported a blank one.</summary>
    public string Player { get; init; } = string.Empty;

    /// <summary>Friendly species name (already resolved from the engine class by the roster service).</summary>
    public string Species { get; init; } = string.Empty;

    /// <summary>Growth from 0 to 1, not a percentage.</summary>
    public float Growth { get; init; }

    /// <summary>
    /// Seconds since this player was last believed to have (re)entered the world, or null when that
    /// is unknown.
    /// </summary>
    public double? TimeAliveSeconds { get; init; }

    /// <summary>The raw timestamp behind <see cref="TimeAliveSeconds"/>, with the same caveats.</summary>
    public DateTimeOffset? SpawnedAt { get; init; }
}

public sealed class ServerRosterDto
{
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>See <see cref="ServerStatusDto.RosterIsStale"/> - the entries below are the last
    /// snapshot either way, so this is what separates "nobody is online" from "we cannot tell".</summary>
    public bool IsStale { get; init; }

    public int StalenessThresholdSeconds { get; init; }

    public IReadOnlyList<ServerRosterEntryDto> Players { get; init; } = [];
}

/// <summary>Read-only server and roster surface for the companion website.</summary>
public static class ServerStatusEndpoints
{
    /// <summary>Three missed refreshes.</summary>
    internal static readonly TimeSpan RosterMaxAge = TimeSpan.FromSeconds(90);

    [WolverineGet("/api/v1/server/status")]
    public static ServerStatusDto Status(
        [NotBody] WorldRosterCache roster, [NotBody] PlayerPresenceManager presence) => new()
    {
        OnlinePlayerCount = roster.OnlineCount,
        LinkedPlayersInGame = presence.GetAllPlayerIds().Count,
        RosterLastUpdatedAt = roster.LastUpdatedAt,
        RosterIsStale = roster.IsStale(RosterMaxAge),
        RosterStalenessThresholdSeconds = (int)RosterMaxAge.TotalSeconds,
    };

    [WolverineGet("/api/v1/server/roster")]
    public static async Task<ServerRosterDto> Roster(
        [NotBody] WorldRosterCache roster,
        [NotBody] PlayerSpawnTracker spawns,
        [NotBody] CancellationToken ct)
    {
        // One read of the volatile field, so the entries and the timestamps below describe the same
        // snapshot even if the refresh loop replaces it mid-request.
        var entries = roster.Entries;

        // The spawn tracker is a per-key distributed cache with no batch read, so this is one
        // lookup per player.
        var spawnedAt = await Task.WhenAll(entries.Select(e => spawns.GetLastSpawnAsync(e.Steam, ct)));

        var now = DateTimeOffset.UtcNow;

        return new ServerRosterDto
        {
            LastUpdatedAt = roster.LastUpdatedAt,
            IsStale = roster.IsStale(RosterMaxAge),
            StalenessThresholdSeconds = (int)RosterMaxAge.TotalSeconds,
            Players = entries.Select((entry, index) => new ServerRosterEntryDto
            {
                Player = string.IsNullOrWhiteSpace(entry.Name) ? "Unknown player" : entry.Name,
                Species = entry.Species,
                Growth = entry.Growth,
                SpawnedAt = spawnedAt[index],
                TimeAliveSeconds = spawnedAt[index] is { } at ? (now - at).TotalSeconds : null,
            }).ToList(),
        };
    }
}
