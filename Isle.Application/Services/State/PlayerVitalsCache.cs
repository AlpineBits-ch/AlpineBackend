using System.Collections.Concurrent;
using System.Text.Json;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Api.Services.State;

/// <summary>One player's dinosaur as the game server last described it.</summary>
/// <param name="Species">The engine class name, unresolved.</param>
/// <param name="Growth">0..1, as the bridge reports it.</param>
/// <param name="X">World coordinates.</param>
public sealed record PlayerVitals(
    string Species,
    double Growth,
    double? Health,
    double? Hunger,
    double? Thirst,
    double? Stamina,
    double X,
    double Y,
    double Z,
    DateTimeOffset ObservedAt);

/// <summary>
/// The most recent stats snapshot for each player, held in Redis with a short TTL.
/// </summary>
public sealed class PlayerVitalsCache(IDistributedCache cache, ILogger<PlayerVitalsCache> logger)
{
    /// <summary>How long a snapshot is worth serving.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

    /// <summary>Minimum spacing between Redis writes for one player. See the class remarks.</summary>
    public static readonly TimeSpan WriteThrottle = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.Ordinal);

    public static string KeyFor(string steamId) => $"isle:vitals:{steamId}";

    /// <summary>
    /// Records a snapshot, unless one was recorded for this player within <see
    /// cref="WriteThrottle"/>.
    /// </summary>
    public async Task CaptureAsync(StatsSnapshot snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Steam) || snapshot.Pos is null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_lastWrite.TryGetValue(snapshot.Steam, out var last) && now - last < WriteThrottle)
            return;

        _lastWrite[snapshot.Steam] = now;

        var vitals = new PlayerVitals(
            Species: snapshot.Species ?? string.Empty,
            Growth: snapshot.Growth,
            Health: Fraction(snapshot.Vitals?.Hp, snapshot.Vitals?.HpMax),
            Hunger: Fraction(snapshot.Vitals?.Hunger, snapshot.Vitals?.HungerMax),
            Thirst: Fraction(snapshot.Vitals?.Thirst, snapshot.Vitals?.ThirstMax),
            Stamina: Fraction(snapshot.Vitals?.Stamina, snapshot.Vitals?.StaminaMax),
            X: snapshot.Pos.X,
            Y: snapshot.Pos.Y,
            Z: snapshot.Pos.Z,
            ObservedAt: now);

        try
        {
            await cache.SetStringAsync(
                KeyFor(snapshot.Steam),
                JsonSerializer.Serialize(vitals),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                ct);
        }
        catch (Exception ex)
        {
            // Let the next tick try again; nothing downstream is waiting on this.
            logger.LogDebug(ex, "Could not cache vitals for {Steam}", snapshot.Steam);

            // Drop the throttle marker so the retry is not also skipped - otherwise one failed write
            // silently costs a whole throttle window.
            _lastWrite.TryRemove(snapshot.Steam, out _);
        }
    }

    /// <summary>
    /// The player's live dinosaur, or null when there is no fresh snapshot: they are offline, they
    /// are dead and have not respawned, or the feed is down.
    /// </summary>
    public async Task<PlayerVitals?> GetAsync(string steamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return null;

        try
        {
            var raw = await cache.GetStringAsync(KeyFor(steamId), ct);
            return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<PlayerVitals>(raw);
        }
        catch (Exception ex)
        {
            // Redis down, or a value written by an older shape.
            logger.LogDebug(ex, "Could not read cached vitals for {Steam}", steamId);
            return null;
        }
    }

    /// <summary>
    /// A vital as a fraction of its maximum, or null when the server reported no maximum.
    /// </summary>
    private static double? Fraction(double? current, double? max) =>
        current is { } value && max is { } ceiling && ceiling > 0 ? value / ceiling : null;
}
