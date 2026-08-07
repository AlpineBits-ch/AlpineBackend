using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Voice.Sessions;

/// <summary>
/// Binds a minted SFU session to the user who minted it, for every service that runs rooms.
/// </summary>
public sealed class SfuSessionOwnership(IDistributedCache cache)
{
    /// <summary>How long an ownership record outlives its last use.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(4);

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = Ttl
    };

    private static string Key(string mediaSessionId) => $"voice:session-owner:{mediaSessionId}";

    /// <summary>Records that <paramref name="userId"/> minted <paramref name="mediaSessionId"/>.
    /// Call this before handing the id back, so no window exists in which a session is usable but
    /// unowned.</summary>
    public Task BindAsync(string mediaSessionId, string userId, CancellationToken ct = default) =>
        cache.SetStringAsync(Key(mediaSessionId), userId, CacheOptions, ct);

    /// <summary>Whether <paramref name="userId"/> is the user who minted
    /// <paramref name="mediaSessionId"/>. A blank or unknown session id is not owned by anybody,
    /// including the caller.</summary>
    public async Task<bool> OwnsAsync(string? mediaSessionId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaSessionId)) return false;

        var owner = await cache.GetStringAsync(Key(mediaSessionId), ct);
        return owner is not null && string.Equals(owner, userId, StringComparison.Ordinal);
    }

    /// <summary>Forgets an ownership record.</summary>
    public Task ReleaseAsync(string mediaSessionId, CancellationToken ct = default) =>
        cache.RemoveAsync(Key(mediaSessionId), ct);
}
