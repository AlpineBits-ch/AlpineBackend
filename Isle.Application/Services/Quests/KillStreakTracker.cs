using StackExchange.Redis;

namespace Isle.Api.Services.Quests;

/// <summary>One player's current unbroken run of kills.</summary>
public sealed record KillStreak(string PlayerId, int Kills, DateTimeOffset LastKillAt);

/// <summary>
/// Session-scoped kill streaks — deliberately separate from <c>KillLog</c>, which is the permanent
/// audit trail. A streak is "how hot is this player <i>right now</i>": it decays out of a rolling
/// window and is wiped the moment they die or disconnect, because a spree that ended is not a spree.
///
/// <para>Backed by a Redis hash (counts) plus a sorted set (last-kill timestamps) rather than one key
/// per player, because the spree rules need the whole <i>leaderboard</i>, not a single lookup: a
/// player only gets marked if they are clearly ahead of everyone else, so we must be able to read
/// second place. Both keys carry a TTL so an abandoned server does not leave state behind forever.</para>
/// </summary>
public sealed class KillStreakTracker(IConnectionMultiplexer redis, ILogger<KillStreakTracker> logger)
{
    private const string CountsKey = "isle:spree:kills";
    private const string SeenKey = "isle:spree:last";

    /// <summary>Kills stop counting toward a streak once they are this old.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(6);

    private IDatabase Db => redis.GetDatabase();

    /// <summary>Records a kill and returns the killer's streak length, or null if Redis was unreachable.</summary>
    public async Task<int?> RegisterKillAsync(string playerId)
    {
        try
        {
            await PruneAsync();

            var kills = await Db.HashIncrementAsync(CountsKey, playerId, 1);
            await Db.SortedSetAddAsync(SeenKey, playerId, Now());

            await Db.KeyExpireAsync(CountsKey, KeyTtl);
            await Db.KeyExpireAsync(SeenKey, KeyTtl);

            return (int)kills;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record kill streak for {PlayerId}", playerId);
            return null;
        }
    }

    /// <summary>Clears a player's streak. Called on death and on disconnect — both end the run.</summary>
    public async Task ResetAsync(string playerId)
    {
        try
        {
            await Db.HashDeleteAsync(CountsKey, playerId);
            await Db.SortedSetRemoveAsync(SeenKey, playerId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reset kill streak for {PlayerId}", playerId);
        }
    }

    public async Task<int> GetAsync(string playerId)
    {
        try
        {
            await PruneAsync();
            var value = await Db.HashGetAsync(CountsKey, playerId);
            return value.HasValue && int.TryParse(value.ToString(), out var kills) ? kills : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read kill streak for {PlayerId}", playerId);
            return 0;
        }
    }

    /// <summary>Every live streak, highest first. This is what the "must be clearly ahead" rule reads.</summary>
    public async Task<IReadOnlyList<KillStreak>> GetLeaderboardAsync()
    {
        try
        {
            await PruneAsync();

            var counts = await Db.HashGetAllAsync(CountsKey);
            if (counts.Length == 0)
                return [];

            var seen = await Db.SortedSetRangeByScoreWithScoresAsync(SeenKey);
            var lastSeen = seen.ToDictionary(e => e.Element.ToString(), e => e.Score);

            return counts
                .Select(entry =>
                {
                    if (!int.TryParse(entry.Value.ToString(), out var kills))
                        return null;

                    var playerId = entry.Name.ToString();
                    var at = lastSeen.TryGetValue(playerId, out var score)
                        ? DateTimeOffset.FromUnixTimeSeconds((long)score)
                        : DateTimeOffset.UtcNow;

                    return new KillStreak(playerId, kills, at);
                })
                .OfType<KillStreak>()
                .OrderByDescending(s => s.Kills)
                .ThenByDescending(s => s.LastKillAt)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read kill streak leaderboard");
            return [];
        }
    }

    /// <summary>Wipes every streak. Used on <c>!questadmin</c> reset and to clear state after a wipe.</summary>
    public async Task ClearAllAsync()
    {
        try
        {
            await Db.KeyDeleteAsync(CountsKey);
            await Db.KeyDeleteAsync(SeenKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear kill streaks");
        }
    }

    /// <summary>
    /// Drops streaks whose last kill fell out of the window. Runs on read rather than on a timer:
    /// kills are infrequent enough that this is cheaper than another background loop, and it keeps
    /// "expired" and "observed" from ever disagreeing.
    /// </summary>
    private async Task PruneAsync()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(Window).ToUnixTimeSeconds();

        var stale = await Db.SortedSetRangeByScoreAsync(SeenKey, double.NegativeInfinity, cutoff);
        if (stale.Length == 0)
            return;

        await Db.HashDeleteAsync(CountsKey, stale);
        await Db.SortedSetRemoveAsync(SeenKey, stale);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
