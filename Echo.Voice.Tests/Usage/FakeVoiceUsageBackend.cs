using Echo.Voice.Usage;

namespace Echo.Voice.Tests.Usage;

/// <summary>A <see cref="TimeProvider"/> the test drives by hand, so an interval is a statement
/// rather than a wait.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>An in-memory <see cref="IVoiceUsageBackend"/>.</summary>
public sealed class FakeVoiceUsageBackend(FakeClock clock) : IVoiceUsageBackend
{
    private readonly Dictionary<string, (long Value, DateTimeOffset Expires)> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _sets = new(StringComparer.Ordinal);

    /// <summary>Every operation throws, modelling Redis being unreachable.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Only the write of the counters throws, modelling a failure after the sample has
    /// already been claimed - the harder half, because the meter is mid-sequence.</summary>
    public bool FailAccumulate { get; set; }

    public int Claims { get; private set; }
    public int Accumulations { get; private set; }

    public Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        Guard();
        if (_counters.TryGetValue(key, out var held) && held.Expires > clock.Now)
            return Task.FromResult(false);

        _counters[key] = (1, clock.Now + ttl);
        Claims++;
        return Task.FromResult(true);
    }

    public Task<long?> ReadCounterAsync(string key, CancellationToken ct)
    {
        Guard();
        if (!_counters.TryGetValue(key, out var entry) || entry.Expires <= clock.Now)
            return Task.FromResult<long?>(null);
        return Task.FromResult<long?>(entry.Value);
    }

    public Task WriteCounterAsync(string key, long value, TimeSpan ttl, CancellationToken ct)
    {
        Guard();
        _counters[key] = (value, clock.Now + ttl);
        return Task.CompletedTask;
    }

    public Task AccumulateAsync(
        string hashKey, IReadOnlyList<KeyValuePair<string, long>> deltas, TimeSpan ttl,
        CancellationToken ct)
    {
        Guard();
        if (FailAccumulate) throw new InvalidOperationException("accumulate failed");

        if (!_hashes.TryGetValue(hashKey, out var hash))
            _hashes[hashKey] = hash = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (field, delta) in deltas)
            hash[field] = hash.GetValueOrDefault(field) + delta;

        Accumulations++;
        return Task.CompletedTask;
    }

    public Task AddToSetAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct)
    {
        Guard();
        if (!_sets.TryGetValue(setKey, out var set))
            _sets[setKey] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(member);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, long>> ReadHashAsync(string hashKey, CancellationToken ct)
    {
        Guard();
        return Task.FromResult<IReadOnlyDictionary<string, long>>(
            _hashes.TryGetValue(hashKey, out var hash)
                ? new Dictionary<string, long>(hash, StringComparer.Ordinal)
                : new Dictionary<string, long>(StringComparer.Ordinal));
    }

    public Task<IReadOnlyList<string>> ReadSetAsync(string setKey, CancellationToken ct)
    {
        Guard();
        return Task.FromResult<IReadOnlyList<string>>(
            _sets.TryGetValue(setKey, out var set) ? set.ToList() : []);
    }

    /// <summary>Whether anything at all was written, for the cases that must record nothing.</summary>
    public bool WroteNothing => _hashes.Count == 0 && _sets.Count == 0;

    private void Guard()
    {
        if (Unreachable) throw new InvalidOperationException("redis unreachable");
    }
}
