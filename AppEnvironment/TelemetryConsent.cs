using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppEnvironment;

/// <summary>
/// Resolves data-collection consent for a batch of accounts, however the calling service happens to
/// be able to. Returns one entry per account it could answer for; an id that is absent from the
/// result is left unresolved, which the snapshot treats as "no consent".
/// </summary>
/// <param name="services">A scoped provider - the privacy-settings caches are scoped services.</param>
public delegate Task<IReadOnlyDictionary<string, bool>> TelemetryConsentResolver(
    IServiceProvider services,
    IReadOnlyList<string> userIds,
    CancellationToken ct);

/// <summary>
/// The synchronous answer to "may this account be identified in telemetry?" for the services that
/// do not own the consent record (T0-4 of docs/specs/privacy.md).
/// </summary>
public sealed class TelemetryConsentSnapshot
{
    private sealed class Entry
    {
        public bool Consented;
        public bool Resolved;
        public DateTimeOffset ResolvedAt;
        public DateTimeOffset LastAskedAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _entryLifetime;
    private readonly TimeSpan _idleEviction;
    private readonly int _maxTracked;

    public TelemetryConsentSnapshot(
        TimeProvider? clock = null,
        TimeSpan? entryLifetime = null,
        TimeSpan? idleEviction = null,
        int? maxTracked = null)
    {
        _clock = clock ?? TimeProvider.System;
        _entryLifetime = entryLifetime ?? Env.TelemetryConsent.EntryLifetime;
        _idleEviction = idleEviction ?? TimeSpan.FromMinutes(10);
        _maxTracked = maxTracked ?? Env.TelemetryConsent.MaxTrackedUsers;
    }

    /// <summary>
    /// The delegate handed to <see cref="SentryPrivacy.HasDataCollectionConsent"/>.
    /// </summary>
    public bool Has(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        var now = _clock.GetUtcNow();

        if (_entries.TryGetValue(userId, out var entry))
        {
            entry.LastAskedAt = now;
            return entry.Resolved && now - entry.ResolvedAt <= _entryLifetime && entry.Consented;
        }

        // Bounded: an unauthenticated burst of failing requests across many accounts must not be
        // able to grow this table.
        if (_entries.Count < _maxTracked)
        {
            _entries.TryAdd(userId, new Entry { LastAskedAt = now });
        }

        return false;
    }

    /// <summary>Records a resolved answer.</summary>
    public void Set(string userId, bool consented)
    {
        if (string.IsNullOrEmpty(userId)) return;

        var now = _clock.GetUtcNow();
        var entry = _entries.GetOrAdd(userId, _ => new Entry { LastAskedAt = now });
        entry.Consented = consented;
        entry.Resolved = true;
        entry.ResolvedAt = now;
    }

    /// <summary>Drops an account back to "unknown", which is to say back to "no consent" until it is
    /// resolved again.</summary>
    public void Forget(string userId) => _entries.TryRemove(userId, out _);

    /// <summary>
    /// The ids the next refresh pass should resolve, and the point at which accounts nothing has
    /// asked about in a while stop being tracked. Called only by the refresh loop.
    /// </summary>
    public IReadOnlyList<string> TakeRefreshSet()
    {
        var now = _clock.GetUtcNow();
        var due = new List<string>();

        foreach (var (userId, entry) in _entries)
        {
            if (now - entry.LastAskedAt > _idleEviction)
            {
                _entries.TryRemove(userId, out _);
                continue;
            }

            due.Add(userId);
        }

        return due;
    }

    public int TrackedCount => _entries.Count;
}

/// <summary>
/// Keeps a <see cref="TelemetryConsentSnapshot"/> current and installs it behind <see
/// cref="SentryPrivacy.HasDataCollectionConsent"/>.
/// </summary>
public sealed class TelemetryConsentRefreshService(
    IServiceScopeFactory scopeFactory,
    TelemetryConsentSnapshot snapshot,
    TelemetryConsentResolver resolver,
    ILogger<TelemetryConsentRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SentryPrivacy.HasDataCollectionConsent = snapshot.Has;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Refreshing the telemetry consent snapshot failed");
            }

            try
            {
                await Task.Delay(Env.TelemetryConsent.RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One refresh pass.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var wanted = snapshot.TakeRefreshSet();
        if (wanted.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var answers = await resolver(scope.ServiceProvider, wanted, ct);

        foreach (var (userId, consented) in answers)
        {
            snapshot.Set(userId, consented);
        }
    }
}

public static class TelemetryConsentExtensions
{
    /// <summary>
    /// Wires <see cref="SentryPrivacy.HasDataCollectionConsent"/> to this service's own view of the
    /// privacy record.
    /// </summary>
    public static IServiceCollection AddTelemetryConsentGate(
        this IServiceCollection services, TelemetryConsentResolver resolver)
    {
        services.AddSingleton(_ => new TelemetryConsentSnapshot());
        services.AddSingleton(resolver);
        services.AddHostedService<TelemetryConsentRefreshService>();
        return services;
    }
}
