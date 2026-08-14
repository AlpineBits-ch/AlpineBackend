using System.Collections.Concurrent;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.Logging;

namespace Echo.Entitlements.Caching;

/// <summary>The resolver, with Redis in front of it (spec section 4.3).</summary>
public class CachedEntitlementResolver : EntitlementResolver
{
    private readonly IEntitlementCacheStore _store;
    private readonly EntitlementCacheKeyspace _keyspace;
    private readonly EntitlementSetCodec _codec;
    private readonly EntitlementCacheOptions _options;
    private readonly ILogger<CachedEntitlementResolver> _logger;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<EntitlementKey> _catalogue;
    private readonly bool _shortCircuited;

    /// <summary>One resolution per subject at a time, per process.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<EntitlementSet>>> _inFlight =
        new(StringComparer.Ordinal);

    public CachedEntitlementResolver(
        IReadOnlyList<IEntitlementSource> sources,
        IEntitlementCacheStore store,
        EntitlementCacheKeyspace keyspace,
        EntitlementSetCodec codec,
        EntitlementCacheOptions options,
        ILogger<CachedEntitlementResolver> logger,
        TimeProvider? clock = null,
        IReadOnlyList<EntitlementKey>? catalogue = null)
        : base(sources, catalogue)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _keyspace = keyspace;
        _codec = codec;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _catalogue = catalogue ?? EntitlementKeys.All;

        _shortCircuited = sources.Count > 0
                          && sources.OrderBy(source => source.Precedence).First().ShortCircuits;
    }

    /// <summary>True when the cache is bypassed entirely, which is the <c>selfhost</c> case. Exposed
    /// so a test can assert the bypass rather than infer it from a store that was never called.
    /// </summary>
    public bool IsBypassed => _shortCircuited;

    public override Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        if (_shortCircuited) return base.ResolveAsync(subject, cancellationToken);

        var key = _keyspace.SetKey(subject);

        // The shared work runs uncancelled on purpose: it is being awaited by callers this one
        // knows nothing about, and letting the first of them to give up cancel the resolution for
        // all of them would turn one abandoned request into a miss for everybody behind it.
        var work = _inFlight.GetOrAdd(key, cacheKey => new Lazy<Task<EntitlementSet>>(
            () => ThroughCacheAsync(subject, cacheKey),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return Await(work, key, cancellationToken);
    }

    private async Task<EntitlementSet> Await(
        Lazy<Task<EntitlementSet>> work, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Removed by whoever finishes first, so the next miss starts a fresh resolution rather
            // than re-awaiting a completed one.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<EntitlementSet>>>(key, work));
        }
    }

    private async Task<EntitlementSet> ThroughCacheAsync(EntitlementSubject subject, string key)
    {
        var now = _clock.GetUtcNow();
        var cached = _codec.Decode(await _store.ReadAsync(key, CancellationToken.None).ConfigureAwait(false));

        if (cached is { } hit && hit.IsFreshAt(now)) return hit.Set;

        try
        {
            var resolved = await base.ResolveAsync(subject, CancellationToken.None).ConfigureAwait(false);
            await StoreAsync(key, resolved, _options.Ttl, _options.Retain).ConfigureAwait(false);
            return resolved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailOpenAsync(subject, key, cached, exception).ConfigureAwait(false);
        }
    }

    /// <summary>What to serve when the sources could not answer.</summary>
    private async Task<EntitlementSet> FailOpenAsync(
        EntitlementSubject subject, string key, CachedEntitlementSet? cached, Exception exception)
    {
        if (cached is { } stale)
        {
            _logger.LogWarning(exception,
                "Could not resolve entitlements for {Subject}. Serving the set last resolved at {FreshFrom}, "
                + "and retrying in {Grace}.",
                subject, stale.FreshUntil - _options.Ttl, _options.OutageGrace);

            await StoreAsync(key, stale.Set, _options.OutageGrace, _options.Retain).ConfigureAwait(false);
            return stale.Set;
        }

        _logger.LogWarning(exception,
            "Could not resolve entitlements for {Subject} and nothing was cached for it. Falling back to "
            + "catalogue defaults, which grant more than any plan rather than less.", subject);

        var defaults = CatalogueDefaults(subject);
        await StoreAsync(key, defaults, _options.OutageGrace, _options.OutageGrace).ConfigureAwait(false);
        return defaults;
    }

    private Task StoreAsync(string key, EntitlementSet set, TimeSpan freshFor, TimeSpan keepFor) =>
        _store.WriteAsync(key, _codec.Encode(set, _clock.GetUtcNow() + freshFor), keepFor, CancellationToken.None);

    /// <summary>Every key of this subject's scope at its declared default.</summary>
    private EntitlementSet CatalogueDefaults(EntitlementSubject subject)
    {
        var entries = new Dictionary<EntitlementKey, EntitlementEntry>();

        foreach (var key in _catalogue)
        {
            if (!key.AppliesTo(subject.Kind)) continue;
            entries[key] = new EntitlementEntry(key, key.Default, EntitlementProvenance.CatalogueDefault);
        }

        return new EntitlementSet(entries);
    }
}
