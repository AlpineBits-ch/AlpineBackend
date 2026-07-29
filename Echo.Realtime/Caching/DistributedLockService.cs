using StackExchange.Redis;

namespace Echo.Realtime.Caching;

/// <summary>A simple per-key mutual-exclusion lock backed by Redis.</summary>
public interface IDistributedLockService
{
    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, waiting up to <paramref name="wait"/> (default
    /// 5s) for it to free up.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan? wait = null, CancellationToken ct = default);
}

public sealed class RedisDistributedLockService(IConnectionMultiplexer redis) : IDistributedLockService
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    // The critical sections this guards are all fast in-process JSON mutations plus a couple of
    // Redis round trips -a lock held this long always means the holder crashed or is stuck, never
    // legitimate work in progress.
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public async Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        RedisKey lockKey = $"lock:{key}";
        RedisValue token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + (wait ?? DefaultWait);

        while (true)
        {
            if (await db.LockTakeAsync(lockKey, token, LockExpiry))
                return new RedisLockHandle(db, lockKey, token);

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Could not acquire distributed lock for '{key}' within {wait ?? DefaultWait}.");

            await Task.Delay(RetryDelay, ct);
        }
    }

    private sealed class RedisLockHandle(IDatabase db, RedisKey lockKey, RedisValue token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await db.LockReleaseAsync(lockKey, token);
        }
    }
}
