using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Api.Services.State;

/// <summary>
/// Tracks roughly when each player last (re)entered the world, so features like friend teleports
/// can gate on "spawned within the last N minutes".
/// <para>
/// The bridge contract exposes no explicit spawn event (only join / leave / death), so this is fed
/// from <c>join</c> (fresh connect → spawn) and <c>death</c> (about to respawn). It is therefore an
/// approximation; combine it with a growth check for a meaningful "fresh spawn" gate.
/// </para>
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
