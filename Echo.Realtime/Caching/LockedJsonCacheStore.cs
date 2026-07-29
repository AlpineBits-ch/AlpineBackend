using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Realtime.Caching;

/// <summary>
/// Wraps the "load a JSON blob from IDistributedCache, mutate it in memory, save it back"
/// pattern used by voice/call state (Call, ChannelVoiceState) with a per-key distributed lock,
/// so two concurrent requests touching the same entry (e.g. one participant accepting a call
/// while another publishes their audio track) can't silently lose one side's write to a
/// last-writer-wins overwrite of the whole blob.
/// </summary>
public sealed class LockedJsonCacheStore(IDistributedLockService locks, IDistributedCache cache)
{
    /// <summary>
    /// Loads the entity at <paramref name="cacheKey"/>, applies <paramref name="mutate"/>, and
    /// saves it back -all while holding the lock for <paramref name="lockKey"/>. Returns the
    /// mutated entity, or null if nothing was found at <paramref name="cacheKey"/> (in which
    /// case <paramref name="mutate"/> is never invoked and nothing is saved).
    /// </summary>
    public async Task<T?> UpdateAsync<T>(
        string lockKey,
        string cacheKey,
        Action<T> mutate,
        DistributedCacheEntryOptions options,
        CancellationToken ct = default) where T : class
    {
        await using var _ = await locks.AcquireAsync(lockKey, ct: ct);

        var entity = await LoadAsync<T>(cacheKey, ct);
        if (entity is null) return null;

        mutate(entity);
        await SaveAsync(cacheKey, entity, options, ct);
        return entity;
    }

    /// <summary>
    /// Plain load with no locking -safe for read-only call sites, and for composing a
    /// caller-managed critical section across more than one key (see
    /// GuildVoiceStateHandler's move-between-channels handler, which must hold two locks
    /// at once and so can't go through <see cref="UpdateAsync{T}"/>).
    /// </summary>
    public async Task<T?> LoadAsync<T>(string cacheKey, CancellationToken ct = default) where T : class
    {
        var raw = await cache.GetStringAsync(cacheKey, ct);
        return raw is null ? null : JsonSerializer.Deserialize<T>(raw);
    }

    /// <summary>Plain save with no locking -see <see cref="LoadAsync{T}"/>.</summary>
    public Task SaveAsync<T>(string cacheKey, T entity, DistributedCacheEntryOptions options, CancellationToken ct = default) where T : class
    {
        return cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(entity), options, ct);
    }
}
