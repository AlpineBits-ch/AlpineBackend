using System.Globalization;
using StackExchange.Redis;

namespace Isle.Api.Services.Quests;

/// <summary>One player's showing at a quest location.</summary>
/// <param name="SteamId">The player, unresolved - the ledger never touches the database.</param>
/// <param name="Ticks">How many roster samples found them inside the quest's region.</param>
/// <param name="FirstSeenAt">When they first turned up, which is the order participants are ranked in.</param>
public sealed record QuestPresence(string SteamId, int Ticks, DateTimeOffset FirstSeenAt);

/// <summary>Who actually went to a quest, and how long they stayed.</summary>
public sealed class QuestProgressLedger(IConnectionMultiplexer redis, ILogger<QuestProgressLedger> logger)
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(12);

    private IDatabase Db => redis.GetDatabase();

    private static string PresenceKey(string questInstanceId) => $"isle:quest:presence:{questInstanceId}";

    private static string FirstSeenKey(string questInstanceId) => $"isle:quest:firstseen:{questInstanceId}";

    /// <summary>Credits one sample to everyone in <paramref name="steamIds"/>.</summary>
    public async Task CreditPresenceAsync(string questInstanceId, IEnumerable<string> steamIds)
    {
        var present = steamIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (present.Count == 0)
            return;

        try
        {
            var presenceKey = PresenceKey(questInstanceId);
            var firstSeenKey = FirstSeenKey(questInstanceId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var batch = Db.CreateBatch();

            var writes = new List<Task>(present.Count * 2 + 2);
            foreach (var steam in present)
            {
                writes.Add(batch.HashIncrementAsync(presenceKey, steam));

                // NotExists: the first sighting is the arrival.
                writes.Add(batch.HashSetAsync(firstSeenKey, steam, now, When.NotExists));
            }

            writes.Add(batch.KeyExpireAsync(presenceKey, KeyTtl));
            writes.Add(batch.KeyExpireAsync(firstSeenKey, KeyTtl));

            batch.Execute();
            await Task.WhenAll(writes);
        }
        catch (Exception ex)
        {
            // A missed sample costs one player one tick of credit; it must never take down the tick.
            logger.LogWarning(ex, "Could not credit quest presence for {InstanceId}", questInstanceId);
        }
    }

    /// <summary>
    /// Everyone who was seen at least <paramref name="minTicks"/> times, earliest arrival first.
    /// </summary>
    public async Task<IReadOnlyList<QuestPresence>> GetQualifiedAsync(string questInstanceId, int minTicks)
    {
        try
        {
            var presence = await Db.HashGetAllAsync(PresenceKey(questInstanceId));
            if (presence.Length == 0)
                return [];

            var firstSeen = (await Db.HashGetAllAsync(FirstSeenKey(questInstanceId)))
                .Where(e => e.Value.HasValue)
                .ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

            var visitors = presence
                .Select(entry =>
                {
                    var steam = entry.Name.ToString();
                    if (!TryParseTicks(entry.Value, out var ticks))
                        return null;

                    // A missing arrival time means the HSETNX was the write that failed.
                    var at = firstSeen.TryGetValue(steam, out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : DateTimeOffset.UtcNow;

                    return new QuestPresence(steam, ticks, at);
                })
                .OfType<QuestPresence>()
                .ToList();

            var qualified = visitors
                .Where(p => p.Ticks >= minTicks)
                .OrderBy(p => p.FirstSeenAt)
                .ThenByDescending(p => p.Ticks)
                .ToList();

            // The dwell bar is a guess at how long "stay a while" should mean, and a quest that
            // pays nobody looks identical whether nobody went or everybody left too early.
            if (visitors.Count != qualified.Count)
                logger.LogDebug("Quest {InstanceId}: {Qualified} of {Total} visitors stayed {Threshold} " +
                                "ticks. Samples: {Samples}",
                    questInstanceId, qualified.Count, visitors.Count, minTicks,
                    string.Join(", ", visitors.Select(v => $"{v.SteamId}={v.Ticks}")));

            return qualified;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read quest presence for {InstanceId}", questInstanceId);
            return [];
        }
    }

    /// <summary>How many players are currently credited with any presence at all. Drives the <c>!quests</c> headcount.</summary>
    public async Task<int> VisitorCountAsync(string questInstanceId)
    {
        try
        {
            return (int)await Db.HashLengthAsync(PresenceKey(questInstanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not count quest visitors for {InstanceId}", questInstanceId);
            return 0;
        }
    }

    /// <summary>Drops the ledger for a closed quest. Called from the shared teardown, so it runs on every exit path.</summary>
    public async Task ClearAsync(string questInstanceId)
    {
        try
        {
            await Db.KeyDeleteAsync(PresenceKey(questInstanceId));
            await Db.KeyDeleteAsync(FirstSeenKey(questInstanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the quest ledger for {InstanceId}", questInstanceId);
        }
    }

    /// <summary>Reads an <c>HINCRBY</c> counter.</summary>
    private static bool TryParseTicks(RedisValue value, out int ticks)
    {
        ticks = 0;
        return value.HasValue
               && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
    }
}
