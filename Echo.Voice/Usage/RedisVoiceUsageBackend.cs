using StackExchange.Redis;

namespace Echo.Voice.Usage;

/// <summary>
/// <see cref="IVoiceUsageBackend"/> over the shared Redis connection every service that runs voice
/// rooms already holds.
/// </summary>
public sealed class RedisVoiceUsageBackend(IConnectionMultiplexer redis) : IVoiceUsageBackend
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct) =>
        await Db.StringSetAsync(key, 1, ttl, When.NotExists);

    public async Task<long?> ReadCounterAsync(string key, CancellationToken ct)
    {
        var value = await Db.StringGetAsync(key);
        return value.TryParse(out long parsed) ? parsed : null;
    }

    public Task WriteCounterAsync(string key, long value, TimeSpan ttl, CancellationToken ct) =>
        Db.StringSetAsync(key, value, ttl);

    public async Task AccumulateAsync(
        string hashKey, IReadOnlyList<KeyValuePair<string, long>> deltas, TimeSpan ttl,
        CancellationToken ct)
    {
        // Batched rather than awaited one at a time: this runs on every sample of every live room,
        // and a per-field round trip would make the meter's cost scale with the number of track
        // kinds in the room for no reason.
        var batch = Db.CreateBatch();
        var pending = new List<Task>(deltas.Count + 1);
        foreach (var (field, delta) in deltas)
            pending.Add(batch.HashIncrementAsync(hashKey, field, delta));

        // Refreshed on every write rather than set once at creation, so the retention window runs
        // from the guild's last activity.
        pending.Add(batch.KeyExpireAsync(hashKey, ttl));
        batch.Execute();
        await Task.WhenAll(pending);
    }

    public async Task AddToSetAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct)
    {
        await Db.SetAddAsync(setKey, member);
        await Db.KeyExpireAsync(setKey, ttl);
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadHashAsync(
        string hashKey, CancellationToken ct)
    {
        var entries = await Db.HashGetAllAsync(hashKey);
        var result = new Dictionary<string, long>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Value.TryParse(out long value))
                result[entry.Name.ToString()] = value;
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ReadSetAsync(string setKey, CancellationToken ct)
    {
        var members = await Db.SetMembersAsync(setKey);
        return members.Select(m => m.ToString()).ToList();
    }
}

/// <summary>The backend used when no Redis connection is registered.</summary>
public sealed class DisabledVoiceUsageBackend : IVoiceUsageBackend
{
    public Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<long?> ReadCounterAsync(string key, CancellationToken ct) =>
        Task.FromResult<long?>(null);

    public Task WriteCounterAsync(string key, long value, TimeSpan ttl, CancellationToken ct) =>
        Task.CompletedTask;

    public Task AccumulateAsync(
        string hashKey, IReadOnlyList<KeyValuePair<string, long>> deltas, TimeSpan ttl,
        CancellationToken ct) => Task.CompletedTask;

    public Task AddToSetAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, long>> ReadHashAsync(string hashKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

    public Task<IReadOnlyList<string>> ReadSetAsync(string setKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
