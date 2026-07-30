using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Services;

/// <summary>
/// Per-channel slowmode: the minimum gap a member must leave between two messages in the same
/// channel.
/// </summary>
public static class SlowModeGuard
{
    private static string LastSendKey(string channelId, string userId) => $"slowmode:{channelId}:{userId}";

    /// <summary>
    /// Returns the seconds the author must still wait, or null when they may post now.
    /// </summary>
    public static async Task<double?> CheckAsync(string channelId, string userId, int slowModeSeconds, IDistributedCache cache)
    {
        if (slowModeSeconds <= 0) return null;

        var key = LastSendKey(channelId, userId);
        var raw = await cache.GetStringAsync(key);

        if (raw is not null && long.TryParse(raw, out var lastTicks))
        {
            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(lastTicks, TimeSpan.Zero);
            var remaining = slowModeSeconds - elapsed.TotalSeconds;

            // A negative remaining means the key outlived its window (clock skew between the
            // writer and this pod, or a TTL rounded up) - treat that as clear rather than
            // stranding the author behind a stale key.
            if (remaining > 0) return Math.Round(remaining, 2);
        }

        await MarkSentAsync(channelId, userId, slowModeSeconds, cache);
        return null;
    }

    /// <summary>Records an accepted send.</summary>
    public static Task MarkSentAsync(string channelId, string userId, int slowModeSeconds, IDistributedCache cache) =>
        cache.SetStringAsync(LastSendKey(channelId, userId), DateTimeOffset.UtcNow.UtcTicks.ToString(),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(slowModeSeconds),
            });
}
