using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Persistence;

namespace Isle.Domain.Entity;

/// <summary>One continuous stretch of a player being in the world, on one dinosaur.</summary>
public class PlaySession : BaseEntity<PlaySession>, IPrefixedEntity
{
    public static string Prefix { get; } = "play";

    /// <summary>
    /// How long a session may go without a confirmed heartbeat before it is written off as
    /// abandoned.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(2);

    public string PlayerId { get; set; } = string.Empty;
    public virtual Player? Player { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// The last moment the game server confirmed this player was still in the world.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Settled length, written once on close.</summary>
    public long DurationSeconds { get; set; }

    /// <summary>The dinosaur being played, as the game server reports the class.</summary>
    public string? Species { get; set; }

    /// <summary>Null exactly while the session is open.</summary>
    public PlaySessionEndReason? EndReason { get; set; }

    public bool IsOpen => EndedAt is null;

    /// <summary>True when the hard cap applies: still open, and nothing has confirmed the player for
    /// longer than <see cref="AbandonedAfter"/>.</summary>
    public bool IsAbandoned(DateTimeOffset now) => IsOpen && now - LastSeenAt >= AbandonedAfter;

    /// <summary>How long this session has counted for, at <paramref name="now"/>.</summary>
    public long ElapsedSeconds(DateTimeOffset now)
    {
        if (!IsOpen) return DurationSeconds;

        var until = LastSeenAt > now ? now : LastSeenAt;
        return Seconds(StartedAt, until);
    }

    public static PlaySession Open(string playerId, string? species, DateTimeOffset now) => new()
    {
        Id = GenerateId(),
        CreatedAt = now,
        UpdatedAt = now,
        PlayerId = playerId,
        Species = NormaliseSpecies(species),
        StartedAt = now,
        LastSeenAt = now,
    };

    /// <summary>
    /// Records that the game server still has this player in the world, and the species it reports
    /// them on.
    /// </summary>
    public void Touch(DateTimeOffset now, string? species = null)
    {
        if (now > LastSeenAt) LastSeenAt = now;

        if (Species is null && NormaliseSpecies(species) is { } observed)
            Species = observed;
    }

    /// <summary>
    /// Whether <paramref name="species"/> is a different dinosaur from the one this session is
    /// recording, i.e. whether the session has to be split.
    /// </summary>
    public bool IsSpeciesChange(string? species) =>
        NormaliseSpecies(species) is { } observed && Species is not null && !string.Equals(Species, observed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes the session at <paramref name="endedAt"/>, clamped into [<see cref="StartedAt"/>,
    /// <see cref="LastSeenAt"/>] so no caller can talk this row into a negative length or into
    /// counting past what the roster actually confirmed.
    /// </summary>
    public bool Close(PlaySessionEndReason reason, DateTimeOffset endedAt)
    {
        if (!IsOpen) return false;

        var at = endedAt;
        if (at > LastSeenAt) at = LastSeenAt;
        if (at < StartedAt) at = StartedAt;

        EndedAt = at;
        EndReason = reason;
        DurationSeconds = Seconds(StartedAt, at);
        UpdatedAt = DateTimeOffset.UtcNow;

        return true;
    }

    private static long Seconds(DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = (long)(to - from).TotalSeconds;
        return seconds < 0 ? 0 : seconds;
    }

    private static string? NormaliseSpecies(string? species) =>
        string.IsNullOrWhiteSpace(species) ? null : species.Trim();
}
