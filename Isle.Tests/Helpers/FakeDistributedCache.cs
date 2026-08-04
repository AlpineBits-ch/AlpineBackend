using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Isle.Tests.Helpers;

/// <summary>
/// In-process dictionary-backed <see cref="IDistributedCache"/> for unit tests, with hooks to seed
/// and inspect entries directly and to make the store throw on demand (the fail-closed paths).
/// Same shape as Messaging.Tests/Guild.Tests/Social.Tests use for their privacy caches.
/// </summary>
internal sealed class FakeDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    /// <summary>When true every read and write throws, standing in for Redis being unreachable.</summary>
    public bool Broken { get; set; }

    public void SetEntry(string key, string value) => _store[key] = Encoding.UTF8.GetBytes(value);

    public bool HasEntry(string key) => _store.ContainsKey(key);

    public string? ReadEntry(string key) =>
        _store.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value) : null;

    public int Count => _store.Count;

    // ── IDistributedCache ─────────────────────────────────────────────────────

    public byte[]? Get(string key)
    {
        if (Broken) throw new InvalidOperationException("redis is down");
        return _store.TryGetValue(key, out var value) ? value : null;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (Broken) throw new InvalidOperationException("redis is down");
        _store[key] = value;
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key)
    {
        if (Broken) throw new InvalidOperationException("redis is down");
        _store.Remove(key);
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
