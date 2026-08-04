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
/// The synchronous answer to "may this account be identified in telemetry?" for the services that do
/// not own the consent record (T0-4 of docs/specs/privacy.md).
///
/// <para><b>Why a mirror at all.</b> <see cref="SentryPrivacy.HasDataCollectionConsent"/> is a
/// synchronous delegate invoked from inside the Sentry SDK's <c>BeforeSend</c>. Social, Guild and
/// Messaging hold their copy of the privacy record in a Redis-backed <c>PrivacySettingsCache</c>
/// whose API is asynchronous and which falls back to a bus call on a miss. Neither is callable from
/// that callback: blocking on Redis - let alone on a request/reply to Identity - while reporting an
/// exception turns the error path into a second failure mode. So the async lookup is done ahead of
/// time, on a background loop, and only the boolean is read on the error path.</para>
///
/// <para><b>Identity's shape, adapted, not a fourth pattern.</b> Identity owns the table, so its
/// <c>DataCollectionConsentSnapshot</c> can hold the whole consenting set and enumerate it every
/// minute. These three services cannot enumerate anything - they can only ask about ids they already
/// know - so the set is populated by the ids telemetry actually asks about: the first event for an
/// account is pseudonymized (fail closed), the account is queued, and by the next event the real
/// answer is in hand. Since <c>AllowDataCollection</c> is opt-in and defaults false, "no consent" is
/// also the correct answer for the overwhelming majority of accounts that will ever be asked
/// about.</para>
///
/// <para><b>Every unknown is a no.</b> Not tracked, not yet resolved, resolution failed, entry aged
/// past <see cref="TelemetryConsentConfiguration.EntryLifetime"/>, tracking table full - all of them
/// answer false. The cost of a wrong "no" is a pseudonymized stack trace. The cost of a wrong "yes"
/// is a user's email address in a third-party error tracker, which cannot be recalled.</para>
///
/// <para><b>Withdrawal.</b> Identity publishes <c>UserPrivacySettingsChangedEvent</c> on every write
/// and each of the three services already evicts its Redis entry from that event, so the
/// authoritative value behind this mirror is fresh immediately. What this class adds is a bound on
/// its own staleness: an entry is re-resolved every
/// <see cref="TelemetryConsentConfiguration.RefreshInterval"/> (15s by default) and stops being
/// trusted entirely after <see cref="TelemetryConsentConfiguration.EntryLifetime"/> (45s), so a
/// withdrawal takes effect within seconds rather than waiting out the 5-to-10-minute cache TTL - and
/// a refresh loop that has stopped working degrades towards pseudonymization rather than towards
/// stale consent.</para>
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
    /// The delegate handed to <see cref="SentryPrivacy.HasDataCollectionConsent"/>. Never throws,
    /// never blocks, never allocates a lookup - and registers interest in an account it has not been
    /// asked about before, so the refresh loop picks it up.
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
        // able to grow this table. Past the ceiling the id is simply never tracked, and an untracked
        // id is a non-consenting one.
        if (_entries.Count < _maxTracked)
        {
            _entries.TryAdd(userId, new Entry { LastAskedAt = now });
        }

        return false;
    }

    /// <summary>Records a resolved answer. Public so the refresh loop - and a test - can populate the
    /// mirror without going through <see cref="Has"/>.</summary>
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
/// Keeps a <see cref="TelemetryConsentSnapshot"/> current and installs it behind
/// <see cref="SentryPrivacy.HasDataCollectionConsent"/>.
///
/// <para>The install happens before the first refresh, and is safe: an empty snapshot answers "no
/// consent" for everyone, which is exactly the right behaviour for a process that has just started
/// and knows nothing about anybody's preferences yet.</para>
///
/// <para>A failed pass is logged and the previous answers are left alone rather than cleared - "we
/// could not check" is not "they withdrew" - but they are not left alone indefinitely either: each
/// entry carries its resolution time and stops being trusted once it is older than
/// <see cref="TelemetryConsentConfiguration.EntryLifetime"/>. A refresh loop that stays broken
/// therefore converges on pseudonymizing everything.</para>
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

    /// <summary>One refresh pass. Public so it can be exercised directly rather than by starting a
    /// host and waiting out an interval - the same reason the retention sweeps are split from their
    /// hosted services.</summary>
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
    /// privacy record. Call it from a service that consumes Identity's privacy settings but does not
    /// own them; Identity itself has its own, table-backed, snapshot.
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
