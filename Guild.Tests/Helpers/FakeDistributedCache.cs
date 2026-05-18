using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Tests.Helpers;

/// <summary>In-process dictionary-backed distributed cache for unit tests.</summary>
internal sealed class FakeDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _store = new();

    public void SetEntry(string key, string value) =>
        _store[key] = Encoding.UTF8.GetBytes(value);

    public bool HasEntry(string key) => _store.ContainsKey(key);

    // ── IDistributedCache ─────────────────────────────────────────────────────

    public byte[]? Get(string key) =>
        _store.TryGetValue(key, out var value) ? value : null;

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        _store[key] = value;

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        Task.CompletedTask;

    public void Remove(string key) => _store.Remove(key);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
