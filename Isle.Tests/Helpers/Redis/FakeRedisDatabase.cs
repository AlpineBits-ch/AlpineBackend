using StackExchange.Redis;

namespace Isle.Tests.Helpers.Redis;

/// <summary>
/// Hand-rolled fake of the specific <see cref="IDatabase"/> commands the KOTH/quest Redis ledgers
/// actually issue (hash incr/get, sorted sets, strings, sets, TTLs, batches). Every other member of the
/// (large) StackExchange.Redis surface throws — nothing under test should ever reach them, and a throw
/// makes an untested code path loud instead of silently wrong.
/// </summary>
internal sealed partial class FakeRedisDatabase(FakeRedisStore store) : IDatabase
{
    private static NotImplementedException NotSupported([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new($"FakeRedisDatabase does not implement '{member}' — add it if a ledger under test starts using it.");

    // --- Strings ---------------------------------------------------------------------------------

    public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(store.Strings.TryGetValue(key.ToString(), out var value) ? new RedisValue(value) : RedisValue.Null);

    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, When when = When.Always, CommandFlags flags = CommandFlags.None) =>
        StringSetAsync(key, value, expiry, false, when, flags);

    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, bool keepTtl, When when = When.Always, CommandFlags flags = CommandFlags.None)
    {
        store.Strings[key.ToString()] = value.ToString();
        if (expiry is { } ttl)
            store.Expirations[key.ToString()] = ttl;
        return Task.FromResult(true);
    }

    /// <summary>
    /// StackExchange.Redis 3.x's modern conditional-write overload — the classic
    /// <c>(TimeSpan?, When)</c> form above is what the ledgers were written against, but overload
    /// resolution for a plain 3-arg call can land here instead depending on the exact compiled call
    /// shape, so this needs the same real behaviour. <see cref="Expiration"/> exposes no public way to
    /// recover the wrapped duration, which is fine — nothing under test asserts on TTL length, only
    /// that a write happened.
    /// </summary>
    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, Expiration expiry, ValueCondition when, CommandFlags flags = CommandFlags.None)
    {
        if (when.Equals(ValueCondition.NotExists) && store.Strings.ContainsKey(key.ToString()))
            return Task.FromResult(false);

        store.Strings[key.ToString()] = value.ToString();
        return Task.FromResult(true);
    }

    // --- Keys ------------------------------------------------------------------------------------

    public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(store.DeleteKey(key.ToString()));

    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None)
    {
        if (expiry is { } ttl)
            store.Expirations[key.ToString()] = ttl;
        return Task.FromResult(store.KeyExists(key.ToString()));
    }

    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, ExpireWhen expireWhen, CommandFlags flags = CommandFlags.None) =>
        KeyExpireAsync(key, expiry, flags);

    // --- Hashes ------------------------------------------------------------------------------------

    private System.Collections.Concurrent.ConcurrentDictionary<string, string> Hash(string key) =>
        store.Hashes.GetOrAdd(key, _ => new());

    /// <summary>Emulates HINCRBY/HINCRBYFLOAT: read-current, add, compare-and-swap until it sticks.</summary>
    private static double Increment(System.Collections.Concurrent.ConcurrentDictionary<string, string> hash, string field, double amount)
    {
        while (true)
        {
            var hasCurrent = hash.TryGetValue(field, out var raw);
            var current = hasCurrent && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0d;
            var next = current + amount;
            var nextRaw = next.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var swapped = hasCurrent ? hash.TryUpdate(field, nextRaw, raw!) : hash.TryAdd(field, nextRaw);
            if (swapped)
                return next;
        }
    }

    public Task<long> HashIncrementAsync(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult((long)Increment(Hash(key.ToString()), hashField.ToString(), value));

    public Task<double> HashIncrementAsync(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(Increment(Hash(key.ToString()), hashField.ToString(), value));

    public Task<bool> HashSetAsync(RedisKey key, RedisValue hashField, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
    {
        var field = hashField.ToString();
        var hash = store.Hashes.GetOrAdd(key.ToString(), _ => new());

        if (when == When.NotExists)
            return Task.FromResult(hash.TryAdd(field, value.ToString()));

        hash[field] = value.ToString();
        return Task.FromResult(true);
    }

    public Task<RedisValue> HashGetAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
    {
        if (store.Hashes.TryGetValue(key.ToString(), out var hash) && hash.TryGetValue(hashField.ToString(), out var value))
            return Task.FromResult(new RedisValue(value));

        return Task.FromResult(RedisValue.Null);
    }

    public Task<HashEntry[]> HashGetAllAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        if (!store.Hashes.TryGetValue(key.ToString(), out var hash))
            return Task.FromResult(Array.Empty<HashEntry>());

        var entries = hash.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
        return Task.FromResult(entries);
    }

    public Task<bool> HashDeleteAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
    {
        if (store.Hashes.TryGetValue(key.ToString(), out var hash))
            return Task.FromResult(hash.TryRemove(hashField.ToString(), out _));

        return Task.FromResult(false);
    }

    public Task<long> HashDeleteAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
    {
        if (!store.Hashes.TryGetValue(key.ToString(), out var hash))
            return Task.FromResult(0L);

        var removed = hashFields.Count(f => hash.TryRemove(f.ToString(), out _));
        return Task.FromResult((long)removed);
    }

    public Task<long> HashLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(store.Hashes.TryGetValue(key.ToString(), out var hash) ? (long)hash.Count : 0L);

    // --- Sorted sets -------------------------------------------------------------------------------

    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, CommandFlags flags = CommandFlags.None) =>
        SortedSetAddAsync(key, member, score, SortedSetWhen.Always, flags);

    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, SortedSetWhen when, CommandFlags flags = CommandFlags.None)
    {
        var set = store.SortedSets.GetOrAdd(key.ToString(), _ => new());
        set[member.ToString()] = score;
        return Task.FromResult(true);
    }

    /// <summary>The classic <see cref="When"/>-based sibling of the overload above — see the note on
    /// <c>StringSetAsync(Expiration, ValueCondition, ...)</c> for why both need real behaviour.</summary>
    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, When when, CommandFlags flags = CommandFlags.None) =>
        SortedSetAddAsync(key, member, score, SortedSetWhen.Always, flags);

    public Task<bool> SortedSetRemoveAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
    {
        if (store.SortedSets.TryGetValue(key.ToString(), out var set))
            return Task.FromResult(set.TryRemove(member.ToString(), out _));

        return Task.FromResult(false);
    }

    public Task<long> SortedSetRemoveAsync(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
    {
        if (!store.SortedSets.TryGetValue(key.ToString(), out var set))
            return Task.FromResult(0L);

        var removed = members.Count(m => set.TryRemove(m.ToString(), out _));
        return Task.FromResult((long)removed);
    }

    public Task<RedisValue[]> SortedSetRangeByScoreAsync(
        RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity,
        Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1,
        CommandFlags flags = CommandFlags.None)
    {
        if (!store.SortedSets.TryGetValue(key.ToString(), out var set))
            return Task.FromResult(Array.Empty<RedisValue>());

        var query = set.Where(kv => kv.Value >= start && kv.Value <= stop);
        query = order == Order.Ascending ? query.OrderBy(kv => kv.Value) : query.OrderByDescending(kv => kv.Value);

        var values = query.Skip((int)skip);
        if (take >= 0)
            values = values.Take((int)take);

        return Task.FromResult(values.Select(kv => new RedisValue(kv.Key)).ToArray());
    }

    public Task<SortedSetEntry[]> SortedSetRangeByScoreWithScoresAsync(
        RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity,
        Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1,
        CommandFlags flags = CommandFlags.None)
    {
        if (!store.SortedSets.TryGetValue(key.ToString(), out var set))
            return Task.FromResult(Array.Empty<SortedSetEntry>());

        var query = set.Where(kv => kv.Value >= start && kv.Value <= stop);
        query = order == Order.Ascending ? query.OrderBy(kv => kv.Value) : query.OrderByDescending(kv => kv.Value);

        var values = query.Skip((int)skip);
        if (take >= 0)
            values = values.Take((int)take);

        return Task.FromResult(values.Select(kv => new SortedSetEntry(kv.Key, kv.Value)).ToArray());
    }

    // --- Sets ---------------------------------------------------------------------------------------

    public Task<long> SetAddAsync(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
    {
        var set = store.Sets.GetOrAdd(key.ToString(), _ => new());
        var added = 0L;
        foreach (var value in values)
            if (set.TryAdd(value.ToString(), true))
                added++;

        return Task.FromResult(added);
    }

    public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
    {
        var set = store.Sets.GetOrAdd(key.ToString(), _ => new());
        return Task.FromResult(set.TryAdd(value.ToString(), true));
    }

    public Task<long> SetLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(store.Sets.TryGetValue(key.ToString(), out var set) ? (long)set.Count : 0L);

    // --- Batches --------------------------------------------------------------------------------

    public IBatch CreateBatch(object? asyncState = null) => new FakeRedisBatch(store);
}
