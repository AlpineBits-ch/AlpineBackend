using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Services;

/// <summary>Per-channel slowmode: the minimum gap a member must leave between two messages in the
/// same channel. Sibling of <see cref="AutoModeration"/> and deliberately shaped the same way -
/// static, Redis-backed, called inline from the send path - but kept separate because the two
/// answer different questions: auto-mod asks "may this content be posted at all", slowmode asks
/// "may this author post right now".
///
/// The window itself lives on Guild's Channel row and arrives on
/// HasUserPermissionToChannelResponse.SlowModeSeconds, so this type never talks to the bus.</summary>
public static class SlowModeGuard
{
    private static string LastSendKey(string channelId, string userId) => $"slowmode:{channelId}:{userId}";

    /// <summary>Returns the seconds the author must still wait, or null when they may post now.
    ///
    /// The stored value is the UTC tick count of the last accepted send, with the key's TTL set to
    /// the window - so an expired key and "never posted" are the same state and no sweep is needed.
    /// The remaining time is computed from the stored timestamp rather than inferred from the key
    /// still existing, because Redis TTL resolution is coarser than the sub-second answer the
    /// client needs for its countdown.</summary>
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

    /// <summary>Records an accepted send. Split out of <see cref="CheckAsync"/> so the timestamp can
    /// be written by tests directly, and so a future caller that needs check-without-consume has
    /// somewhere to hang it.</summary>
    public static Task MarkSentAsync(string channelId, string userId, int slowModeSeconds, IDistributedCache cache) =>
        cache.SetStringAsync(LastSendKey(channelId, userId), DateTimeOffset.UtcNow.UtcTicks.ToString(),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(slowModeSeconds),
            });
}
