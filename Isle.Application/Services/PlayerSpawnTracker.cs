using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Api.Services;

/// <summary>
/// Tracks roughly when each player last (re)entered the world, so features like friend teleports
/// can gate on "spawned within the last N minutes".
/// </summary>
public sealed class PlayerSpawnTracker(IDistributedCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    private static string Key(string steam) => $"isle:spawn:{steam}";

    public Task MarkSpawnedAsync(string steam, CancellationToken ct = default)
    {
        var value = DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return cache.SetStringAsync(Key(steam), value,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }, ct);
    }

    public async Task<DateTimeOffset?> GetLastSpawnAsync(string steam, CancellationToken ct = default)
    {
        var value = await cache.GetStringAsync(Key(steam), ct);
        if (value is null || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            return null;

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
