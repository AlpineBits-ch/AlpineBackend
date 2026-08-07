using Echo.Realtime.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Echo.Voice.Rooms;

/// <summary>
/// A room mutation could not be applied because the room stayed contended for the whole retry
/// budget.
/// </summary>
public sealed class VoiceRoomContentionException(VoiceRoomKey key, int attempts, Exception? inner = null)
    : Exception($"Voice room {key} stayed contended across {attempts} attempts", inner)
{
    public VoiceRoomKey Key { get; } = key;
    public int Attempts { get; } = attempts;
}

/// <summary>
/// The only thing in the codebase that can read or write a <see cref="VoiceRoom"/>.
/// </summary>
public sealed class VoiceRoomStore(
    LockedJsonCacheStore store,
    IDistributedLockService locks,
    IDistributedCache cache,
    ILogger<VoiceRoomStore> logger)
{
    /// <summary>Rooms outlive any plausible session but not a forgotten one.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(4);

    /// <summary>Backoff between attempts to take a contended room.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(700),
    ];

    /// <summary>How long to wait for the lock on any single attempt.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(750);

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = Ttl
    };

    /// <summary>Reads a room without locking.</summary>
    public Task<VoiceRoom?> LoadAsync(VoiceRoomKey key, CancellationToken ct = default) =>
        store.LoadAsync<VoiceRoom>(key.CacheKey, ct);

    /// <summary>
    /// Load-or-create, mutate, bump, save, under the room's lock, retrying while the room is
    /// contended.
    /// </summary>
    public Task<VoiceRoom> MutateAsync(
        VoiceRoomKey key,
        Action<VoiceRoom> mutate,
        string? guildId = null,
        CancellationToken ct = default) =>
        MutateCoreAsync(key, mutate, createIfMissing: true, guildId, ct)!;

    /// <summary>Mutates only if the room already exists, returning null otherwise.</summary>
    public Task<VoiceRoom?> MutateExistingAsync(
        VoiceRoomKey key,
        Action<VoiceRoom> mutate,
        CancellationToken ct = default) =>
        MutateCoreAsync(key, mutate, createIfMissing: false, guildId: null, ct);

#pragma warning disable CS8619 // createIfMissing: true never yields null; the public wrappers encode that.
    private async Task<VoiceRoom?> MutateCoreAsync(
        VoiceRoomKey key,
        Action<VoiceRoom> mutate,
        bool createIfMissing,
        string? guildId,
        CancellationToken ct)
    {
        Exception? lastFailure = null;

        for (var attempt = 0; attempt <= RetryDelays.Count; attempt++)
        {
            try
            {
                await using var _ = await locks.AcquireAsync(key.CacheKey, LockWait, ct);

                var room = await store.LoadAsync<VoiceRoom>(key.CacheKey, ct);

                if (room is null)
                {
                    if (!createIfMissing) return null;
                    room = new VoiceRoom { RoomId = key.Id, Kind = key.Kind, GuildId = guildId };
                }

                mutate(room);

                // Unconditional.
                room.Version++;

                await store.SaveAsync(key.CacheKey, room, CacheOptions, ct);
                return room;
            }
            catch (TimeoutException ex) when (attempt < RetryDelays.Count)
            {
                lastFailure = ex;
                logger.LogWarning(
                    "Voice room {Room} contended on attempt {Attempt} of {Total}, retrying in {DelayMs}ms",
                    key, attempt + 1, RetryDelays.Count + 1, RetryDelays[attempt].TotalMilliseconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (TimeoutException ex)
            {
                lastFailure = ex;
            }
        }

        logger.LogError(lastFailure,
            "Voice room {Room} stayed contended across every attempt - the caller's change was not applied",
            key);
        throw new VoiceRoomContentionException(key, RetryDelays.Count + 1, lastFailure);
    }
#pragma warning restore CS8619

    /// <summary>Drops a room outright - the call ended, or the last participant left.</summary>
    public async Task RemoveAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        await using var _ = await locks.AcquireAsync(key.CacheKey, LockWait, ct);
        await cache.RemoveAsync(key.CacheKey, ct);
    }
}
