using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Realtime.Caching;

/// <summary>One viewer of one screen share, with the moment their claim goes stale.</summary>
public sealed class StreamViewer
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>When this claim expires unless the viewer re-announces.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>The whole viewer table for one scope (a voice channel, or a 1:1/group call).</summary>
public sealed class StreamViewers
{
    /// <summary>shareId -&gt; the users currently watching it.</summary>
    public Dictionary<string, List<StreamViewer>> Shares { get; set; } = new();
}

/// <summary>
/// Who is watching whose screen share, for both guild voice channels and direct calls.
/// </summary>
public sealed class StreamViewerStore(IDistributedLockService locks, IDistributedCache cache)
{
    /// <summary>How long a watch claim survives without being re-announced.</summary>
    public static readonly TimeSpan ViewerTtl = TimeSpan.FromSeconds(90);

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    /// <summary>Overridable clock - the TTL is the whole behaviour here, so tests need to move
    /// time rather than sleep through it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Viewer scope for a guild voice channel.</summary>
    public static string ChannelScope(string channelId) => $"channel:{channelId}";

    /// <summary>Viewer scope for a direct call.</summary>
    public static string CallScope(string callId) => $"call:{callId}";

    private static string CacheKey(string scope) => $"stream:viewers:{scope}";

    /// <summary>Records <paramref name="userId"/> as watching <paramref name="shareId"/>, or
    /// refreshes an existing claim. Returns the scope's viewers after the write.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> WatchAsync(
        string scope, string shareId, string userId, CancellationToken ct = default) =>
        MutateAsync(scope, viewers =>
        {
            if (!viewers.Shares.TryGetValue(shareId, out var list))
            {
                list = [];
                viewers.Shares[shareId] = list;
            }

            var expiresAt = Clock.GetUtcNow() + ViewerTtl;
            var existing = list.FirstOrDefault(v => v.UserId == userId);
            if (existing is not null) existing.ExpiresAt = expiresAt;
            else list.Add(new StreamViewer { UserId = userId, ExpiresAt = expiresAt });
        }, ct);

    /// <summary>Drops <paramref name="userId"/>'s claim on one share.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> UnwatchAsync(
        string scope, string shareId, string userId, CancellationToken ct = default) =>
        MutateAsync(scope, viewers =>
        {
            if (viewers.Shares.TryGetValue(shareId, out var list))
                list.RemoveAll(v => v.UserId == userId);
        }, ct);

    /// <summary>Drops every claim <paramref name="userId"/> holds in this scope - they left the
    /// channel or the call, so they are watching nothing in it by definition.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RemoveViewerAsync(
        string scope, string userId, CancellationToken ct = default) =>
        MutateAsync(scope, viewers =>
        {
            foreach (var list in viewers.Shares.Values) list.RemoveAll(v => v.UserId == userId);
        }, ct);

    /// <summary>Forgets a share entirely - it stopped, so its audience is not merely empty, it is
    /// meaningless.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RemoveShareAsync(
        string scope, string shareId, CancellationToken ct = default) =>
        MutateAsync(scope, viewers => viewers.Shares.Remove(shareId), ct);

    /// <summary>Forgets several shares at once - a participant who was streaming just left.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RemoveSharesAsync(
        string scope, IEnumerable<string> shareIds, CancellationToken ct = default)
    {
        var ids = shareIds.ToList();
        return MutateAsync(scope, viewers =>
        {
            foreach (var id in ids) viewers.Shares.Remove(id);
        }, ct);
    }

    /// <summary>Current viewers, with expired claims dropped from the answer (but not rewritten -
    /// a read stays a read; the next write prunes for real).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> SnapshotAsync(
        string scope, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(CacheKey(scope), ct);
        if (raw is null) return EmptySnapshot;

        var viewers = System.Text.Json.JsonSerializer.Deserialize<StreamViewers>(raw);
        if (viewers is null) return EmptySnapshot;

        Prune(viewers);
        return Project(viewers);
    }

    /// <summary>Discards the whole table - the call ended, so nothing in it can still be true.</summary>
    public Task DropAsync(string scope, CancellationToken ct = default) =>
        cache.RemoveAsync(CacheKey(scope), ct);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptySnapshot =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Load-or-create, mutate, prune, save - under the scope's lock.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> MutateAsync(
        string scope, Action<StreamViewers> mutate, CancellationToken ct)
    {
        var key = CacheKey(scope);
        await using var _ = await locks.AcquireAsync(key, ct: ct);

        var raw = await cache.GetStringAsync(key, ct);
        var viewers = raw is null
            ? new StreamViewers()
            : System.Text.Json.JsonSerializer.Deserialize<StreamViewers>(raw) ?? new StreamViewers();

        mutate(viewers);
        Prune(viewers);

        await cache.SetStringAsync(key, System.Text.Json.JsonSerializer.Serialize(viewers), CacheOptions, ct);
        return Project(viewers);
    }

    private void Prune(StreamViewers viewers)
    {
        var now = Clock.GetUtcNow();
        foreach (var list in viewers.Shares.Values) list.RemoveAll(v => v.ExpiresAt <= now);
        foreach (var shareId in viewers.Shares.Where(s => s.Value.Count == 0).Select(s => s.Key).ToList())
            viewers.Shares.Remove(shareId);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Project(StreamViewers viewers) =>
        viewers.Shares.ToDictionary(
            s => s.Key,
            s => (IReadOnlyList<string>)s.Value.Select(v => v.UserId).ToList());
}
