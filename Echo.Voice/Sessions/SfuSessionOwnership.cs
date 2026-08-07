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

    private static string Key(string cfSessionId) => $"voice:session-owner:{cfSessionId}";

    /// <summary>
    /// The per-service keys this replaces, still read (never written) so that sessions minted
    /// before this shipped keep working.
    /// </summary>
    private static readonly string[] LegacyKeys =
    [
        "cf-session-owner:",       // Messaging.Application.Controllers.CloudflareController
        "guild-cf-session-owner:", // Guild.Application.Controllers.GuildCloudflareController
    ];

    /// <summary>Records that <paramref name="userId"/> minted <paramref name="cfSessionId"/>.
    /// Call this before handing the id back, so no window exists in which a session is usable but
    /// unowned.</summary>
    public Task BindAsync(string cfSessionId, string userId, CancellationToken ct = default) =>
        cache.SetStringAsync(Key(cfSessionId), userId, CacheOptions, ct);

    /// <summary>Whether <paramref name="userId"/> is the user who minted
    /// <paramref name="cfSessionId"/>. A blank or unknown session id is not owned by anybody,
    /// including the caller.</summary>
    public async Task<bool> OwnsAsync(string? cfSessionId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfSessionId)) return false;

        var owner = await cache.GetStringAsync(Key(cfSessionId), ct);
        if (owner is not null)
            return string.Equals(owner, userId, StringComparison.Ordinal);

        foreach (var prefix in LegacyKeys)
        {
            var legacyOwner = await cache.GetStringAsync(prefix + cfSessionId, ct);
            if (legacyOwner is null) continue;

            // Found under a pre-migration key.
            await BindAsync(cfSessionId, legacyOwner, ct);
            return string.Equals(legacyOwner, userId, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Forgets an ownership record.</summary>
    public Task ReleaseAsync(string cfSessionId, CancellationToken ct = default) =>
        cache.RemoveAsync(Key(cfSessionId), ct);
}
