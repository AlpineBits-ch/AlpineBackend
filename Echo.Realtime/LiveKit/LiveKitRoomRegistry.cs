using System.Globalization;
using Echo.Realtime.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Echo.Realtime.LiveKit;

/// <summary>Which node each room lives on.</summary>
public sealed class LiveKitRoomRegistry(
    LiveKitOptions options,
    ILogger<LiveKitRoomRegistry> logger,
    IConnectionMultiplexer? redis = null,
    IDistributedLockService? locks = null)
{
    public const string HashKey = "voice:livekit:rooms";

    /// <summary>The key the reconcile pass claims so that only one pod in the deployment sweeps per
    /// interval. Not a mutex: a claim that expires on its own is right for work that may simply be
    /// skipped, and a held lock across a fleet-wide listing would outlive the lock service's own
    /// expiry.</summary>
    private const string SweepKey = "voice:livekit:sweep";

    /// <summary>How long to wait for a room's placement lock.</summary>
    private static readonly TimeSpan PlacementLockWait = TimeSpan.FromSeconds(2);

    private IDatabase? Db => redis?.GetDatabase();

    private static string LockKey(string room) => $"voice:livekit:place:{room}";

    /// <summary>
    /// Finds or places a room, and creates it on the node that answers - all under one lock.
    /// </summary>
    public async Task<LiveKitNode> PlaceAsync(
        string room,
        string region,
        Func<LiveKitNode, CancellationToken, Task> create,
        CancellationToken ct = default)
    {
        var handle = await TryLockAsync(LockKey(room), PlacementLockWait, ct);

        try
        {
            var node = await FindAsync(room, ct) ?? await ClaimAsync(room, region, ct);
            await create(node, ct);
            return node;
        }
        finally
        {
            if (handle is not null) await handle.DisposeAsync();
        }
    }

    /// <summary>Records that a room belongs to a region, and answers the node hosting it.</summary>
    public async Task<LiveKitNode> ClaimAsync(
        string room, string region, CancellationToken ct = default)
    {
        if (Db is not { } db) return Resolve(region, room);

        var value = $"{region}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        if (await db.HashSetAsync(HashKey, room, value, When.NotExists))
            return Resolve(region, room);

        var existing = await db.HashGetAsync(HashKey, room);
        var claimed = RegionOf(existing);
        if (claimed is not null && !string.Equals(claimed, region, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Room {Room} was already claimed for region {Claimed}; keeping it there rather than "
                + "honouring the requested {Requested}", room, claimed, region);
        }

        return Resolve(claimed ?? region, room);
    }

    /// <summary>
    /// The node hosting an existing room, or null when this side has no record of it.
    /// </summary>
    public async Task<LiveKitNode?> FindAsync(string room, CancellationToken ct = default)
    {
        if (Db is not { } db) return options.SoleNode;

        var value = await db.HashGetAsync(HashKey, room);
        if (RegionOf(value) is not { } region) return null;

        var node = options.Node(region);
        if (node is null)
            logger.LogError(
                "Room {Room} is recorded in region {Region}, which no configured node serves - "
                + "nobody can join it until that node is back in LIVEKIT__NODES", room, region);

        return node;
    }

    /// <summary>Drops a room's row outright.</summary>
    public async Task ForgetAsync(string room, CancellationToken ct = default)
    {
        if (Db is { } db) await db.HashDeleteAsync(HashKey, room);
    }

    /// <summary>
    /// Drops a room's row only if it is still older than <paramref name="grace"/> when the lock is
    /// held, and reports whether it did.
    /// </summary>
    public async Task<bool> ForgetIfStaleAsync(
        string room, TimeSpan grace, CancellationToken ct = default)
    {
        if (Db is not { } db) return false;

        var handle = await TryLockAsync(LockKey(room), PlacementLockWait, ct);

        try
        {
            var claimed = await ClaimedAtAsync(room, ct);
            if (claimed is null) return false;
            if (claimed > DateTimeOffset.UtcNow - grace) return false;

            return await db.HashDeleteAsync(HashKey, room);
        }
        finally
        {
            if (handle is not null) await handle.DisposeAsync();
        }
    }

    /// <summary>Every room this side believes exists, and where.</summary>
    public async Task<IReadOnlyDictionary<string, string>> EntriesAsync(CancellationToken ct = default)
    {
        if (Db is not { } db) return new Dictionary<string, string>(StringComparer.Ordinal);

        var entries = await db.HashGetAllAsync(HashKey);
        var result = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (RegionOf(entry.Value) is { } region) result[entry.Name.ToString()] = region;
        }
        return result;
    }

    /// <summary>Claims the right to run one reconcile pass, for <paramref name="ttl"/>.</summary>
    public async Task<bool> TryClaimSweepAsync(TimeSpan ttl, CancellationToken ct = default)
    {
        if (Db is not { } db) return true;
        return await db.StringSetAsync(SweepKey, Environment.MachineName, ttl, When.NotExists);
    }

    /// <summary>The lock, or null when it could not be taken.</summary>
    private async Task<IAsyncDisposable?> TryLockAsync(
        string key, TimeSpan wait, CancellationToken ct)
    {
        if (locks is null) return null;

        try
        {
            return await locks.AcquireAsync(key, wait, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or RedisException)
        {
            logger.LogWarning(
                "Proceeding without the {Key} lock: {Message}. The atomic claim still makes a split "
                + "placement impossible; only the sweep interleaving is unguarded.", key, ex.Message);
            return null;
        }
    }

    /// <summary>The node for a region, or the fleet's only node when it does not resolve.</summary>
    private LiveKitNode Resolve(string region, string room) =>
        options.Node(region)
        ?? options.SoleNode
        ?? throw new InvalidOperationException(
            $"No LiveKit node serves region '{region}', so room '{room}' cannot be placed.");

    private static string? RegionOf(RedisValue value)
    {
        if (value.IsNullOrEmpty) return null;
        var raw = value.ToString();
        var separator = raw.IndexOf('|');
        var region = separator < 0 ? raw : raw[..separator];
        return string.IsNullOrWhiteSpace(region) ? null : region;
    }

    /// <summary>When a registry row was written.</summary>
    public async Task<DateTimeOffset?> ClaimedAtAsync(string room, CancellationToken ct = default)
    {
        if (Db is not { } db) return null;

        var value = await db.HashGetAsync(HashKey, room);
        if (value.IsNullOrEmpty) return null;

        var raw = value.ToString();
        var separator = raw.IndexOf('|');
        return separator >= 0
               && long.TryParse(raw[(separator + 1)..], NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
