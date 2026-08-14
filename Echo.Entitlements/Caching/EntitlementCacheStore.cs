using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Echo.Entitlements.Caching;

/// <summary>The three operations the cache needs from Redis, and nothing else.</summary>
public interface IEntitlementCacheStore
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken);

    Task WriteAsync(string key, string payload, TimeSpan keepFor, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IEntitlementCacheStore"/> over the distributed cache the consuming service already
/// has.
/// </summary>
public sealed class DistributedEntitlementCacheStore(
    IDistributedCache cache, ILogger<DistributedEntitlementCacheStore> logger) : IEntitlementCacheStore
{
    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not read the entitlement cache entry {Key}. Treating it as a miss.", key);
            return null;
        }
    }

    public async Task WriteAsync(
        string key, string payload, TimeSpan keepFor, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetStringAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = keepFor },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not write the entitlement cache entry {Key}. The next read will re-resolve.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not drop the entitlement cache entry {Key}. Its TTL is the backstop.", key);
        }
    }
}

/// <summary>
/// The store used when the consuming service has registered no distributed cache.
/// </summary>
public sealed class DisabledEntitlementCacheStore : IEntitlementCacheStore
{
    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task WriteAsync(
        string key, string payload, TimeSpan keepFor, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
}
