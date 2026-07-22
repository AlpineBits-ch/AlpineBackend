using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Api.Services;

/// <summary>Per-player, per-command cooldown tracking backed by the distributed cache.</summary>
public sealed class CommandCooldownService(IDistributedCache cache)
{
    private static string Key(string playerId, string commandName) => $"isle:cooldown:{commandName}:{playerId}";

    /// <summary>Remaining cooldown for the player/command, or null when they are free to run it.</summary>
    public async Task<TimeSpan?> GetRemainingAsync(string playerId, string commandName, CancellationToken ct = default)
    {
        var value = await cache.GetStringAsync(Key(playerId, commandName), ct);
        if (value is null || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            return null;

        var readyAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        var remaining = readyAt - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>Starts the cooldown window for the player/command. No-op for non-positive durations.</summary>
    public Task StartAsync(string playerId, string commandName, TimeSpan duration, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero) return Task.CompletedTask;

        var readyAt = DateTimeOffset.UtcNow.Add(duration);
        return cache.SetStringAsync(Key(playerId, commandName),
            readyAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration }, ct);
    }
}
