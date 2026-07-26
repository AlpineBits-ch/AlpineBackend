using System.Globalization;
using StackExchange.Redis;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>One player's showing in the hill zone across a match.</summary>
public sealed record KothStanding(string SteamId, int Ticks, DateTimeOffset FirstCreditedAt);

/// <summary>Who has held the hill, how long, and who holds it right now.</summary>
public sealed class KingOfTheHillControlLedger(IConnectionMultiplexer redis, ILogger<KingOfTheHillControlLedger> logger)
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(1);

    private IDatabase Db => redis.GetDatabase();

    private static string ControlKey(string instanceId) => $"isle:koth:control:{instanceId}";
    private static string FirstSeenKey(string instanceId) => $"isle:koth:firstseen:{instanceId}";
    private static string HolderKey(string instanceId) => $"isle:koth:holder:{instanceId}";
    private static string HolderSinceKey(string instanceId) => $"isle:koth:holdersince:{instanceId}";
    private static string ContestantsKey(string instanceId) => $"isle:koth:contestants:{instanceId}";

    /// <summary>Applies one tick's zone presence.</summary>
    public async Task ApplyPresenceAsync(string instanceId, IReadOnlyList<string> steamIdsInZone)
    {
        try
        {
            if (steamIdsInZone.Count > 0)
            {
                await Db.SetAddAsync(ContestantsKey(instanceId), steamIdsInZone.Select(s => (RedisValue)s).ToArray());
                await Db.KeyExpireAsync(ContestantsKey(instanceId), KeyTtl);
            }

            if (steamIdsInZone.Count != 1)
            {
                await ClearHolderAsync(instanceId);
                return;
            }

            var steam = steamIdsInZone[0];
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var batch = Db.CreateBatch();
            var writes = new List<Task>
            {
                batch.HashIncrementAsync(ControlKey(instanceId), steam),

                // NotExists: the first credit is the arrival, used only to break a standings tie.
                batch.HashSetAsync(FirstSeenKey(instanceId), steam, now, When.NotExists),

                batch.KeyExpireAsync(ControlKey(instanceId), KeyTtl),
                batch.KeyExpireAsync(FirstSeenKey(instanceId), KeyTtl),
            };

            batch.Execute();
            await Task.WhenAll(writes);

            var currentHolder = await Db.StringGetAsync(HolderKey(instanceId));
            if (!currentHolder.HasValue || currentHolder != steam)
            {
                // Either the zone was empty/contested last tick, or a different player held it — either
                // way this is a fresh held-alone streak starting now.
                await Db.StringSetAsync(HolderKey(instanceId), steam, KeyTtl);
                await Db.StringSetAsync(HolderSinceKey(instanceId), now, KeyTtl);
            }
            else
            {
                await Db.KeyExpireAsync(HolderKey(instanceId), KeyTtl);
                await Db.KeyExpireAsync(HolderSinceKey(instanceId), KeyTtl);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not credit KOTH control for {InstanceId}", instanceId);
        }
    }

    /// <summary>Drops the held-alone streak without touching anyone's total control ticks.</summary>
    public async Task ClearHolderAsync(string instanceId)
    {
        try
        {
            await Db.KeyDeleteAsync(HolderKey(instanceId));
            await Db.KeyDeleteAsync(HolderSinceKey(instanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the KOTH holder for {InstanceId}", instanceId);
        }
    }

    /// <summary>The current sole holder and how long they have held the zone unbroken, or null if it is empty/contested.</summary>
    public async Task<(string SteamId, TimeSpan Streak)?> GetHolderStreakAsync(string instanceId)
    {
        try
        {
            var holder = await Db.StringGetAsync(HolderKey(instanceId));
            if (!holder.HasValue)
                return null;

            var sinceRaw = await Db.StringGetAsync(HolderSinceKey(instanceId));
            if (!TryParseMs(sinceRaw, out var sinceMs))
                return null;

            var since = DateTimeOffset.FromUnixTimeMilliseconds(sinceMs);
            return (holder.ToString(), DateTimeOffset.UtcNow - since);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the KOTH holder streak for {InstanceId}", instanceId);
            return null;
        }
    }

    /// <summary>Final standings: every player credited at least one tick, most ticks first, earliest arrival breaking a tie.</summary>
    public async Task<IReadOnlyList<KothStanding>> GetStandingsAsync(string instanceId)
    {
        try
        {
            var control = await Db.HashGetAllAsync(ControlKey(instanceId));
            if (control.Length == 0)
                return [];

            var firstSeen = (await Db.HashGetAllAsync(FirstSeenKey(instanceId)))
                .Where(e => e.Value.HasValue)
                .ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

            return control
                .Select(entry =>
                {
                    var steam = entry.Name.ToString();
                    if (!TryParseInt(entry.Value, out var ticks))
                        return null;

                    // A missing arrival time means the HSETNX write was the one that failed.
                    var at = firstSeen.TryGetValue(steam, out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : DateTimeOffset.UtcNow;

                    return new KothStanding(steam, ticks, at);
                })
                .OfType<KothStanding>()
                .OrderByDescending(s => s.Ticks)
                .ThenBy(s => s.FirstCreditedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read KOTH standings for {InstanceId}", instanceId);
            return [];
        }
    }

    /// <summary>
    /// How many distinct players ever set foot in the zone this match, winner included — the basis for
    /// the winner's held-the-hill bonus in <see cref="Isle.Api.Games.KingOfTheHill.KingOfTheHillMode.GetRewards"/>.
    /// </summary>
    public async Task<int> GetContestantCountAsync(string instanceId)
    {
        try
        {
            return (int)await Db.SetLengthAsync(ContestantsKey(instanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the KOTH contestant count for {InstanceId}", instanceId);
            return 0;
        }
    }

    /// <summary>Drops every key for a resolved/cancelled match. Called from the shared teardown, so it runs on every exit path.</summary>
    public async Task ClearAsync(string instanceId)
    {
        try
        {
            await Db.KeyDeleteAsync(ControlKey(instanceId));
            await Db.KeyDeleteAsync(FirstSeenKey(instanceId));
            await Db.KeyDeleteAsync(HolderKey(instanceId));
            await Db.KeyDeleteAsync(HolderSinceKey(instanceId));
            await Db.KeyDeleteAsync(ContestantsKey(instanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the KOTH ledger for {InstanceId}", instanceId);
        }
    }

    private static bool TryParseInt(RedisValue value, out int result)
    {
        result = 0;
        return value.HasValue
               && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseMs(RedisValue value, out long result)
    {
        result = 0;
        return value.HasValue
               && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
