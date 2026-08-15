using System.Text.Json;
using Echo.Realtime.Caching;
using Guild.Application.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Services;

/// <summary>The outcome of asking a ring to change state.</summary>
public readonly record struct VoiceRingTransition(VoiceRing? Ring, bool Transitioned)
{
    /// <summary>The ring does not exist, or its keys have expired out from under it.</summary>
    public bool NotFound => Ring is null;

    /// <summary>The ring exists but somebody else got to it first - a second device, the inviter's
    /// cancel, the expiry sweep.</summary>
    public bool AlreadyResolved => Ring is not null && !Transitioned;
}

/// <summary>
/// Reads and writes the ephemeral voice-ring state: the ring itself, plus the two indexes that make
/// it findable from either end.
/// </summary>
public class VoiceRingStore(IDistributedLockService locks, IDistributedCache cache)
{
    /// <summary>Swappable so a test can make a ring lapse without waiting a minute for it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    private static readonly DistributedCacheEntryOptions Options = new()
    {
        AbsoluteExpirationRelativeToNow = VoiceRing.RetentionTtl,
    };

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public async Task<VoiceRing?> LoadAsync(string ringId, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(VoiceRing.CacheKey(ringId), ct);
        return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<VoiceRing>(raw);
    }

    /// <summary>Writes a new ring and files it under both indexes.</summary>
    public async Task CreateAsync(VoiceRing ring, CancellationToken ct = default)
    {
        await cache.SetStringAsync(VoiceRing.CacheKey(ring.Id), JsonSerializer.Serialize(ring), Options, ct);
        await IndexAsync(VoiceRing.TargetIndexKey(ring.TargetUserId), ring.Id, ct);
        await IndexAsync(VoiceRing.ChannelIndexKey(ring.ChannelId), ring.Id, ct);
    }

    /// <summary>Moves a pending ring to a terminal state, once.</summary>
    public async Task<VoiceRingTransition> ResolveAsync(
        string ringId, VoiceRingStatus status, string? reason, string? deviceId,
        CancellationToken ct = default)
    {
        await using var _ = await locks.AcquireAsync(VoiceRing.CacheKey(ringId), ct: ct);

        var ring = await LoadAsync(ringId, ct);
        if (ring is null) return new VoiceRingTransition(null, false);

        var now = Now;
        if (ring.Status != VoiceRingStatus.Pending) return new VoiceRingTransition(ring, false);

        if (ring.ExpiresAt <= now)
        {
            ring.Status = VoiceRingStatus.Expired;
            ring.Reason = Contracts.VoiceRingReason.TimedOut;
            ring.ResolvedAt = ring.ExpiresAt;
            await cache.SetStringAsync(VoiceRing.CacheKey(ring.Id), JsonSerializer.Serialize(ring), Options, ct);

            // Only the expiry itself may claim an expiry it did not cause.
            return new VoiceRingTransition(ring, status == VoiceRingStatus.Expired);
        }

        ring.Status = status;
        ring.Reason = reason;
        ring.ResolvedAt = now;
        ring.ResolvedByDeviceId = deviceId;

        await cache.SetStringAsync(VoiceRing.CacheKey(ring.Id), JsonSerializer.Serialize(ring), Options, ct);
        return new VoiceRingTransition(ring, true);
    }

    /// <summary>Every live ring asking <paramref name="userId"/> into a channel.</summary>
    public Task<IReadOnlyList<VoiceRing>> PendingForTargetAsync(string userId, CancellationToken ct = default) =>
        PendingAsync(VoiceRing.TargetIndexKey(userId), ct);

    /// <summary>Every live ring pointing into <paramref name="channelId"/>.</summary>
    public Task<IReadOnlyList<VoiceRing>> PendingForChannelAsync(string channelId, CancellationToken ct = default) =>
        PendingAsync(VoiceRing.ChannelIndexKey(channelId), ct);

    private async Task<IReadOnlyList<VoiceRing>> PendingAsync(string indexKey, CancellationToken ct)
    {
        var index = await ReadIndexAsync(indexKey, ct);
        if (index.RingIds.Count == 0) return [];

        var now = Now;
        var live = new List<VoiceRing>();
        var dead = new List<string>();

        foreach (var ringId in index.RingIds)
        {
            var ring = await LoadAsync(ringId, ct);
            if (ring is not null && ring.IsPending(now)) live.Add(ring);
            else dead.Add(ringId);
        }

        if (dead.Count > 0) await UnindexAsync(indexKey, dead, ct);

        return live;
    }

    private async Task IndexAsync(string indexKey, string ringId, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(indexKey, ct: ct);

        var index = await ReadIndexAsync(indexKey, ct);
        if (index.RingIds.Contains(ringId, StringComparer.Ordinal)) return;

        index.RingIds.Add(ringId);
        await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(index), Options, ct);
    }

    private async Task UnindexAsync(string indexKey, IReadOnlyCollection<string> ringIds, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(indexKey, ct: ct);

        // Re-read inside the lock.
        var index = await ReadIndexAsync(indexKey, ct);
        if (index.RingIds.RemoveAll(id => ringIds.Contains(id, StringComparer.Ordinal)) == 0) return;

        if (index.RingIds.Count == 0) await cache.RemoveAsync(indexKey, ct);
        else await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(index), Options, ct);
    }

    private async Task<VoiceRingIndex> ReadIndexAsync(string indexKey, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(indexKey, ct);
        return (string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<VoiceRingIndex>(raw))
               ?? new VoiceRingIndex();
    }
}
