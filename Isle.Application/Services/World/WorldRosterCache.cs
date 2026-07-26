using System.Numerics;
using Isle.Domain.Entity;

namespace Isle.Api.Services.World;

/// <summary>
/// One player as the game server last reported them, with their region already resolved.
/// </summary>
public sealed record RosterEntry(
    string Steam,
    string Name,
    string Species,
    float Growth,
    Vector3 Position,
    string? RegionId,
    string LocationName)
{
    public string Coordinates => RegionMap.FormatCoordinates(Position.X, Position.Y);
}

/// <summary>Last known whole-server roster: who is on, what they are playing, and where.</summary>
public sealed class WorldRosterCache
{
    private volatile IReadOnlyList<RosterEntry> _entries = [];

    public DateTimeOffset? LastUpdatedAt { get; private set; }

    public IReadOnlyList<RosterEntry> Entries => _entries;

    public int OnlineCount => _entries.Count;

    /// <summary>True when no roster read has landed yet, or the last one is older than <paramref name="maxAge"/>.</summary>
    public bool IsStale(TimeSpan maxAge) =>
        LastUpdatedAt is not { } at || DateTimeOffset.UtcNow - at > maxAge;

    public void Replace(IReadOnlyList<RosterEntry> entries)
    {
        _entries = entries;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public RosterEntry? FindBySteam(string? steam) =>
        steam is null ? null : _entries.FirstOrDefault(e => e.Steam == steam);

    /// <summary>Case-insensitive in-game name lookup. Names are not unique; first match wins.</summary>
    public RosterEntry? FindByName(string? name) =>
        name is null
            ? null
            : _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
