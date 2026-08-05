using System.Collections.Concurrent;

namespace Isle.Tests.Helpers.Redis;

/// <summary>
/// In-memory backing store shared by <see cref="FakeRedisDatabase"/> and its batch, so a batched write
/// is visible immediately to the same fake "connection" - real enough for the ledgers under test, which
/// never rely on genuine pipelining/atomicity, only on the end state after <c>await Task.WhenAll(...)</c>.
/// </summary>
internal sealed class FakeRedisStore
{
    public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Hashes { get; } = new();
    public ConcurrentDictionary<string, string> Strings { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, double>> SortedSets { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> Sets { get; } = new();

    // TTLs are tracked but never actually expire anything in tests - nothing under test asserts on
    // expiry timing, only that an expire call was issued without throwing.
    public ConcurrentDictionary<string, TimeSpan> Expirations { get; } = new();

    public bool KeyExists(string key) =>
        Hashes.ContainsKey(key) || Strings.ContainsKey(key) || SortedSets.ContainsKey(key) || Sets.ContainsKey(key);

    public bool DeleteKey(string key)
    {
        var existed = KeyExists(key);
        Hashes.TryRemove(key, out _);
        Strings.TryRemove(key, out _);
        SortedSets.TryRemove(key, out _);
        Sets.TryRemove(key, out _);
        Expirations.TryRemove(key, out _);
        return existed;
    }
}
