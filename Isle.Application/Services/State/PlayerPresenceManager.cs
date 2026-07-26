using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Isle.Api.Services.State;

/// <summary>Set of game-connected playerIds.</summary>
public sealed class PlayerPresenceManager(IConnectionMultiplexer redis, ILogger<PlayerPresenceManager> logger)
{
    private const string Prefix = "isle:presence:";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, byte> _playerIds = new();

    private IDatabase Db => redis.GetDatabase();

    // ---- read (synchronous, served from the local cache) --------------------------------------

    public bool IsPlayerOnline(string playerId) => _playerIds.ContainsKey(playerId);

    // ---- writes (write-through to Redis) -------------------------------------------------------

    /// <summary>Mark a player online (write-through) and (re)set the sliding 2h TTL. Idempotent — also used to refresh.</summary>
    public async Task AddPlayerIdAsync(string playerId)
    {
        _playerIds[playerId] = 0;
        try
        {
            await Db.StringSetAsync(Prefix + playerId, "1", Ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist presence for {PlayerId}", playerId);
        }
    }

    public async Task RemovePlayerIdAsync(string playerId)
    {
        _playerIds.TryRemove(playerId, out _);
        try
        {
            await Db.KeyDeleteAsync(Prefix + playerId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove presence for {PlayerId}", playerId);
        }
    }

    // ---- lifecycle (restart recovery + staleness reconcile) ------------------------------------

    /// <summary>Repopulate the local cache from Redis on startup.</summary>
    public async Task HydrateAsync()
    {
        try
        {
            _playerIds.Clear();
            var count = 0;
            await foreach (var playerId in ReadAllAsync())
            {
                _playerIds[playerId] = 0;
                count++;
            }
            logger.LogInformation("Hydrated {Count} online players from Redis", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to hydrate presence from Redis");
        }
    }

    /// <summary>Rebuild the local cache from Redis, dropping entries whose key has expired (TTL / missed disconnect).</summary>
    public async Task RebuildFromRedisAsync()
    {
        try
        {
            var fresh = new HashSet<string>();
            await foreach (var playerId in ReadAllAsync())
                fresh.Add(playerId);

            foreach (var playerId in _playerIds.Keys)
            {
                if (!fresh.Contains(playerId))
                    _playerIds.TryRemove(playerId, out _);
            }

            foreach (var playerId in fresh)
                _playerIds[playerId] = 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presence reconcile failed");
        }
    }
    public IReadOnlyCollection<string> GetAllPlayerIds() => _playerIds.Keys.ToArray();
    private async IAsyncEnumerable<string> ReadAllAsync()
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        // Presence keys are markers only; the id lives in the key, so no value read is needed.
        await Task.CompletedTask;
        foreach (var key in server.Keys(pattern: Prefix + "*"))
            yield return key.ToString()[Prefix.Length..];
    }
}
