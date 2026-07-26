using StackExchange.Redis;

namespace Isle.Tests.Helpers.Redis;

/// <summary>
/// Fake <see cref="IBatch"/>: forwards to a <see cref="FakeRedisDatabase"/> over the same backing
/// store.
/// </summary>
internal sealed partial class FakeRedisBatch(FakeRedisStore store) : IBatch
{
    private readonly FakeRedisDatabase _inner = new(store);

    private static NotImplementedException NotSupported([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new($"FakeRedisBatch does not implement '{member}' — add it if a ledger under test starts using it.");

    public void Execute()
    {
        // Everything already ran when each *Async method below was called.
    }

    public Task<long> HashIncrementAsync(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None) =>
        _inner.HashIncrementAsync(key, hashField, value, flags);

    public Task<double> HashIncrementAsync(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None) =>
        _inner.HashIncrementAsync(key, hashField, value, flags);

    public Task<bool> HashSetAsync(RedisKey key, RedisValue hashField, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None) =>
        _inner.HashSetAsync(key, hashField, value, when, flags);

    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None) =>
        _inner.KeyExpireAsync(key, expiry, flags);

    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, ExpireWhen expireWhen, CommandFlags flags = CommandFlags.None) =>
        _inner.KeyExpireAsync(key, expiry, expireWhen, flags);
}
