using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;

namespace Echo.Entitlements.Tests;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public static TestClock AtEpoch() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}

/// <summary>
/// Redis, as far as this package is concerned: a dictionary that honours the expiry it was given
/// against the test's clock.
/// </summary>
internal sealed class FakeEntitlementCacheStore(TimeProvider clock) : IEntitlementCacheStore
{
    private readonly Dictionary<string, (string Payload, DateTimeOffset ExpiresAt)> _entries =
        new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    public bool Broken { get; set; }

    public int Writes { get; private set; }

    public int Reads { get; private set; }

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate) return _entries.Keys.ToList();
        }
    }

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reads++;
            if (Broken) return Task.FromResult<string?>(null);

            if (!_entries.TryGetValue(key, out var entry)) return Task.FromResult<string?>(null);

            if (clock.GetUtcNow() >= entry.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(entry.Payload);
        }
    }

    public Task WriteAsync(string key, string payload, TimeSpan keepFor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Writes++;
            if (!Broken) _entries[key] = (payload, clock.GetUtcNow() + keepFor);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!Broken) _entries.Remove(key);
        }

        return Task.CompletedTask;
    }

    public string? Raw(string key)
    {
        lock (_gate) return _entries.TryGetValue(key, out var entry) ? entry.Payload : null;
    }
}

/// <summary>A source that counts how often it was actually asked, can be made to fail the way an
/// unreachable Billing fails, and can be held open so a test can have several callers arrive at once.
/// </summary>
internal sealed class CountingSource(
    EntitlementPrecedence precedence, Func<EntitlementSubject, EntitlementSet> answer) : IEntitlementSource
{
    private int _calls;

    public EntitlementPrecedence Precedence { get; } = precedence;

    public bool Fails { get; set; }

    /// <summary>Held open while a test lines several callers up behind one resolution.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public async Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        if (Gate is { } gate) await gate.Task;

        if (Fails) throw new InvalidOperationException("Billing is unreachable.");

        return answer(subject);
    }

    public static CountingSource Returning(
        EntitlementPrecedence precedence, Action<EntitlementSetBuilder> build)
    {
        var builder = new EntitlementSetBuilder(precedence);
        build(builder);
        var set = builder.Build();
        return new CountingSource(precedence, _ => set);
    }
}

/// <summary>Stands in for Billing's counter, and can stop answering the way an unreachable one
/// does.</summary>
internal sealed class ScriptedVersionProvider(long version) : IEntitlementVersionProvider
{
    private int _calls;

    public long Version { get; set; } = version;

    public bool Fails { get; set; }

    public int Calls => _calls;

    public ValueTask<long> VersionAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        _calls++;
        return Fails
            ? throw new InvalidOperationException("Billing is unreachable.")
            : ValueTask.FromResult(Version);
    }
}
