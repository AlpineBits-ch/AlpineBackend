using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.State;

/// <summary>One player as the roster currently has them, for <see cref="PlaySessionTracker.ReconcileAsync"/>.</summary>
/// <param name="PlayerId">The domain player id - the reconcile pass resolves Steam ids before calling.</param>
/// <param name="Species">The dinosaur the game server reports, or null when it reported none.</param>
public sealed record OnlinePlayer(string PlayerId, string? Species);

/// <summary>What a player's history adds up to.</summary>
/// <param name="TotalSeconds">Every settled second plus whatever the currently open session has confirmed.</param>
/// <param name="FavouriteSpecies">
/// The species with the most seconds behind it, or null when nothing has ever been attributed - a
/// player whose only sessions predate any roster sample naming a class. Derived rather than chosen:
/// a self-declared favourite is a claim, and this is what they actually do.
/// </param>
/// <param name="FirstPlayedAt">When their first session started, or null when they have never played.</param>
public sealed record PlaytimeSummary(long TotalSeconds, string? FavouriteSpecies, DateTimeOffset? FirstPlayedAt)
{
    public static readonly PlaytimeSummary Empty = new(0, null, null);
}

/// <summary>
/// Owns <see cref="PlaySession"/> rows: opens one when a player arrives, closes it when they leave,
/// and - the part that actually matters - closes the ones nobody ever came back to.
/// </summary>
public sealed class PlaySessionTracker(MicroserviceContext context, ILogger<PlaySessionTracker> logger)
{
    /// <summary>
    /// Opens a session for a player who has just arrived, unless one is already open for them.
    /// </summary>
    public async Task StartAsync(string playerId, string? species = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return;

        var now = DateTimeOffset.UtcNow;
        var open = await OpenSessionsAsync(playerId, ct);

        // An already-open session that has gone stale is not the same session: they crashed, and
        // this join is a new arrival.
        var live = open.Where(session => !session.IsAbandoned(now)).ToList();
        foreach (var stale in open.Except(live))
        {
            stale.Close(PlaySessionEndReason.Abandoned, stale.LastSeenAt);
            logger.LogInformation("Closed an abandoned play session for {PlayerId} on re-join, {Seconds}s counted",
                playerId, stale.DurationSeconds);
        }

        if (live.Count > 0)
        {
            foreach (var session in live)
                session.Touch(now, species);
        }
        else
        {
            context.PlaySessions.Add(PlaySession.Open(playerId, species, now));
        }

        await context.SaveChangesAsync(ct);
    }

    /// <summary>Closes a player's session on a reported leave.</summary>
    public async Task EndAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return;

        var open = await OpenSessionsAsync(playerId, ct);
        if (open.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var session in open)
        {
            session.Touch(now);
            session.Close(PlaySessionEndReason.Left, now);
        }

        await context.SaveChangesAsync(ct);
    }

    /// <summary>One reconcile pass over every open session in the service.</summary>
    /// <param name="online">
    /// Everyone the game server currently has in the world, or <c>null</c> when the roster could
    /// not be read or is too old to act on.
    /// </param>
    /// <returns>How many sessions this pass closed, for the caller's log line.</returns>
    public async Task<int> ReconcileAsync(IReadOnlyList<OnlinePlayer>? online, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var openSessions = await context.PlaySessions
            .Where(session => session.EndedAt == null)
            .ToListAsync(ct);

        var roster = online?
            .Where(player => !string.IsNullOrWhiteSpace(player.PlayerId))
            .GroupBy(player => player.PlayerId)
            .ToDictionary(group => group.Key, group => group.First().Species, StringComparer.Ordinal);

        var closed = 0;
        var stillOpen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var session in openSessions)
        {
            // The cap runs first and runs unconditionally.
            if (session.IsAbandoned(now))
            {
                session.Close(PlaySessionEndReason.Abandoned, session.LastSeenAt);
                closed++;
                logger.LogWarning("Play session {SessionId} for {PlayerId} was abandoned; capped at {Seconds}s",
                    session.Id, session.PlayerId, session.DurationSeconds);
                continue;
            }

            if (roster is null)
            {
                stillOpen.Add(session.PlayerId);
                continue;
            }

            if (!roster.TryGetValue(session.PlayerId, out var species))
            {
                // Gone from a roster we trust, with no leave event ever arriving.
                session.Close(PlaySessionEndReason.Disconnected, session.LastSeenAt);
                closed++;
                continue;
            }

            if (session.IsSpeciesChange(species))
            {
                session.Touch(now);
                session.Close(PlaySessionEndReason.SpeciesChange, now);
                closed++;

                context.PlaySessions.Add(PlaySession.Open(session.PlayerId, species, now));
                stillOpen.Add(session.PlayerId);
                continue;
            }

            session.Touch(now, species);
            stillOpen.Add(session.PlayerId);
        }

        // Self-heal, the same way VoicePresenceReconcileService re-adds presence for players the roster
        // confirms: a join event that was dropped, or that landed while this service was restarting,
        // would otherwise mean the player's entire evening is never counted.
        if (roster is not null)
        {
            foreach (var (playerId, species) in roster)
            {
                if (stillOpen.Contains(playerId)) continue;

                context.PlaySessions.Add(PlaySession.Open(playerId, species, now));
                logger.LogInformation("Opened a play session for {PlayerId}, who the roster has in-game with " +
                                      "no session of their own", playerId);
            }
        }

        await context.SaveChangesAsync(ct);
        return closed;
    }

    /// <summary>A player's totals.</summary>
    public async Task<PlaytimeSummary> SummariseAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return PlaytimeSummary.Empty;

        var now = DateTimeOffset.UtcNow;

        var settled = await context.PlaySessions
            .AsNoTracking()
            .Where(session => session.PlayerId == playerId && session.EndedAt != null)
            .GroupBy(session => session.Species)
            .Select(group => new { Species = group.Key, Seconds = group.Sum(session => session.DurationSeconds) })
            .ToListAsync(ct);

        var open = await context.PlaySessions
            .AsNoTracking()
            .Where(session => session.PlayerId == playerId && session.EndedAt == null)
            .ToListAsync(ct);

        var bySpecies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;

        foreach (var row in settled)
        {
            total += row.Seconds;
            if (row.Species is { } species)
                bySpecies[species] = bySpecies.GetValueOrDefault(species) + row.Seconds;
        }

        foreach (var session in open)
        {
            var elapsed = session.ElapsedSeconds(now);
            total += elapsed;
            if (session.Species is { } species)
                bySpecies[species] = bySpecies.GetValueOrDefault(species) + elapsed;
        }

        var first = await context.PlaySessions
            .AsNoTracking()
            .Where(session => session.PlayerId == playerId)
            .OrderBy(session => session.StartedAt)
            .Select(session => (DateTimeOffset?)session.StartedAt)
            .FirstOrDefaultAsync(ct);

        // Ordinal on the name breaks a tie between two species with identical totals, so the answer
        // does not flip between two calls that saw the same data.
        var favourite = bySpecies
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key)
            .FirstOrDefault();

        return new PlaytimeSummary(total, favourite, first);
    }

    /// <summary>Favourite species for a batch of players, for the leaderboard - one grouped query
    /// rather than one round trip per row.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FavouriteSpeciesAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        var favourites = new Dictionary<string, string>(StringComparer.Ordinal);
        if (playerIds.Count == 0) return favourites;

        var totals = await context.PlaySessions
            .AsNoTracking()
            .Where(session => playerIds.Contains(session.PlayerId) && session.Species != null)
            .GroupBy(session => new { session.PlayerId, session.Species })
            .Select(group => new
            {
                group.Key.PlayerId,
                group.Key.Species,
                Seconds = group.Sum(session => session.DurationSeconds),
            })
            .ToListAsync(ct);

        foreach (var group in totals.GroupBy(row => row.PlayerId, StringComparer.Ordinal))
        {
            var top = group
                .OrderByDescending(row => row.Seconds)
                .ThenBy(row => row.Species, StringComparer.Ordinal)
                .First();

            if (top.Species is { } species)
                favourites[group.Key] = species;
        }

        return favourites;
    }

    private Task<List<PlaySession>> OpenSessionsAsync(string playerId, CancellationToken ct) =>
        context.PlaySessions
            .Where(session => session.PlayerId == playerId && session.EndedAt == null)
            .ToListAsync(ct);
}
