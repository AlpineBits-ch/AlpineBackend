using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Isle.Api.Services.State;

/// <summary>Bidirectional steamId &lt;-&gt; playerId(userId) map for voice opt-in.</summary>
public sealed class VoicePlayerRegistry(IConnectionMultiplexer redis, ILogger<VoicePlayerRegistry> logger)
{
    private const string SteamToPlayerPrefix = "isle:voice:s2p:";
    private const string PlayerToSteamPrefix = "isle:voice:p2s:";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);
    // Don't re-bump a steamId's TTL more than once per this window — keeps the hot path off Redis.
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, string> _steamToPlayer = new(); // steamId -> playerId
    private readonly ConcurrentDictionary<string, string> _playerToSteam = new(); // playerId -> steamId
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouch = new(); // steamId -> last TTL bump

    private IDatabase Db => redis.GetDatabase();

    // ---- reads (synchronous, served from the local cache) --------------------------------------

    public bool TryGetPlayerId(string steamId, out string playerId) =>
        _steamToPlayer.TryGetValue(steamId, out playerId!);

    public bool TryGetSteamId(string playerId, out string steamId) =>
        _playerToSteam.TryGetValue(playerId, out steamId!);

    public bool IsPlayerOnline(string? playerId = null, string? steamId = null)
    {
        if (playerId is not null)
            return _playerToSteam.ContainsKey(playerId);

        if (steamId is not null)
            return _steamToPlayer.ContainsKey(steamId);

        throw new ArgumentException("Either playerId or steamId must be provided.");
    }

    // ---- writes (write-through to Redis) -------------------------------------------------------

    public async Task RegisterAsync(string playerId, string steamId)
    {
        _steamToPlayer[steamId] = playerId;
        _playerToSteam[playerId] = steamId;
        _lastTouch[steamId] = DateTimeOffset.UtcNow;

        try
        {
            // Both writes are pipelined on the multiplexer — one logical round trip.
            var s2p = Db.StringSetAsync(SteamToPlayerPrefix + steamId, playerId, Ttl);
            var p2s = Db.StringSetAsync(PlayerToSteamPrefix + playerId, steamId, Ttl);
            await Task.WhenAll(s2p, p2s);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist voice registration for {SteamId}", steamId);
        }
    }

    public async Task UnregisterAsync(string playerId)
    {
        if (!_playerToSteam.TryRemove(playerId, out var steamId))
            return;

        _steamToPlayer.TryRemove(steamId, out _);
        _lastTouch.TryRemove(steamId, out _);
        await DeleteAsync(steamId, playerId);
    }

    public async Task UnregisterBySteamIdAsync(string steamId)
    {
        if (!_steamToPlayer.TryRemove(steamId, out var playerId))
            return;

        _playerToSteam.TryRemove(playerId, out _);
        _lastTouch.TryRemove(steamId, out _);
        await DeleteAsync(steamId, playerId);
    }

    private async Task DeleteAsync(string steamId, string playerId)
    {
        try
        {
            var s2p = Db.KeyDeleteAsync(SteamToPlayerPrefix + steamId);
            var p2s = Db.KeyDeleteAsync(PlayerToSteamPrefix + playerId);
            await Task.WhenAll(s2p, p2s);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove voice registration for {SteamId}", steamId);
        }
    }

    /// <summary>
    /// Slide the 2h TTL for a player proven to still be online (a fresh position snapshot arrived).
    /// </summary>
    public async Task TouchAsync(string steamId)
    {
        if (!_steamToPlayer.TryGetValue(steamId, out var playerId))
            return; // not a voice participant — nothing to keep alive

        var now = DateTimeOffset.UtcNow;
        if (_lastTouch.TryGetValue(steamId, out var last) && now - last < TouchThrottle)
            return;

        _lastTouch[steamId] = now;
        try
        {
            var s2p = Db.KeyExpireAsync(SteamToPlayerPrefix + steamId, Ttl);
            var p2s = Db.KeyExpireAsync(PlayerToSteamPrefix + playerId, Ttl);
            await Task.WhenAll(s2p, p2s);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to refresh voice TTL for {SteamId}", steamId);
        }
    }

    // ---- lifecycle (restart recovery + staleness reconcile) ------------------------------------

    /// <summary>Repopulate the local cache from Redis on startup so a restart doesn't drop the grid.</summary>
    public async Task HydrateAsync()
    {
        try
        {
            var count = 0;
            await foreach (var (steamId, playerId) in ReadAllAsync())
            {
                _steamToPlayer[steamId] = playerId;
                _playerToSteam[playerId] = steamId;
                count++;
            }
            logger.LogInformation("Hydrated {Count} voice registrations from Redis", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to hydrate voice registry from Redis");
        }
    }

    /// <summary>
    /// Rebuild the local cache from Redis, dropping any entry whose key has expired (TTL lapsed or a
    /// missed leave). Keeps the in-memory view honest even though reads never hit Redis directly.
    /// </summary>
    public async Task ReconcileAsync()
    {
        try
        {
            var fresh = new Dictionary<string, string>();
            await foreach (var (steamId, playerId) in ReadAllAsync())
                fresh[steamId] = playerId;

            foreach (var steamId in _steamToPlayer.Keys)
            {
                if (fresh.ContainsKey(steamId))
                    continue;

                if (_steamToPlayer.TryRemove(steamId, out var playerId))
                    _playerToSteam.TryRemove(playerId, out _);
                _lastTouch.TryRemove(steamId, out _);
            }

            foreach (var (steamId, playerId) in fresh)
            {
                _steamToPlayer[steamId] = playerId;
                _playerToSteam[playerId] = steamId;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice registry reconcile failed");
        }
    }

    private async IAsyncEnumerable<(string SteamId, string PlayerId)> ReadAllAsync()
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var db = Db;
        foreach (var key in server.Keys(pattern: SteamToPlayerPrefix + "*"))
        {
            var steamId = key.ToString()[SteamToPlayerPrefix.Length..];
            var playerId = await db.StringGetAsync(key);
            if (!playerId.IsNullOrEmpty)
                yield return (steamId, playerId.ToString());
        }
    }
}
