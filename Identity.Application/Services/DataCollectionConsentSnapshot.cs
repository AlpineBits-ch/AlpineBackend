using System.Collections.Concurrent;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>
/// The set of accounts that have consented to being identified in telemetry, kept in memory so that
/// <c>AppEnvironment.SentryPrivacy.HasDataCollectionConsent</c> - a synchronous delegate called from
/// inside the Sentry SDK's <c>BeforeSend</c> - can be answered without a database round trip.
///
/// <para><b>Why a set of consenters rather than a cache of lookups.</b> The flag is opt-in and
/// defaults false, so the consenting set is the small side of the table: holding "who said yes" is
/// bounded by how many people said yes, while a lazily-populated per-user cache would have to answer
/// a miss synchronously - which from inside a Sentry callback means either blocking on a database
/// call on the error path or inventing an answer. The refresh loop reads only the consenting ids, so
/// the query is over an indexable predicate and returns nothing for a deployment where nobody has
/// opted in.</para>
///
/// <para><b>Fail closed in every direction.</b> An id that is not in the set is treated as
/// non-consenting, which is also what happens before the first refresh completes, and what happens
/// if the refresh throws. The cost of a wrong "no" is a pseudonymized stack trace; the cost of a
/// wrong "yes" is a user's email address in a third-party error tracker, which cannot be taken
/// back.</para>
///
/// <para><b>Withdrawal is immediate, not eventually-consistent.</b> The periodic refresh alone would
/// leave a window after someone turns the flag off in which their identifiers still went out, so
/// <c>UserPrivacySettingsChangedConsentHandler</c> updates the single entry the moment the
/// change event lands. The refresh exists to converge a pod that missed an event or started
/// late.</para>
/// </summary>
public class DataCollectionConsentSnapshot
{
    private readonly ConcurrentDictionary<string, byte> _consented = new();

    /// <summary>True only for an account known to have opted in. Null, unknown and
    /// not-yet-refreshed all answer false.</summary>
    public bool Has(string? userId) =>
        !string.IsNullOrEmpty(userId) && _consented.ContainsKey(userId);

    public void Set(string userId, bool consented)
    {
        if (consented) _consented[userId] = 0;
        else _consented.TryRemove(userId, out _);
    }

    public void Replace(IEnumerable<string> consentingUserIds)
    {
        var fresh = consentingUserIds.ToHashSet();

        foreach (var known in _consented.Keys)
        {
            if (!fresh.Contains(known)) _consented.TryRemove(known, out _);
        }

        foreach (var id in fresh) _consented[id] = 0;
    }

    public int Count => _consented.Count;
}

/// <summary>
/// Keeps <see cref="DataCollectionConsentSnapshot"/> honest, and installs it as the consent lookup
/// behind <c>SentryPrivacy</c> (T0-4's hook, wired here because Identity is the service that owns the
/// consent record).
/// </summary>
public class DataCollectionConsentRefreshService(
    IServiceScopeFactory scopeFactory,
    DataCollectionConsentSnapshot snapshot,
    ILogger<DataCollectionConsentRefreshService> logger) : BackgroundService
{
    /// <summary>Convergence interval. Short because the only thing it corrects is a missed change
    /// event, and long enough that it is one small query a minute rather than a poll.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Installed before the first refresh, and safe to: the snapshot is empty, so every answer is
        // "no consent" until it has actually been loaded. That is the correct behaviour for a service
        // that has just started and does not yet know who consented to what.
        AppEnvironment.SentryPrivacy.HasDataCollectionConsent = snapshot.Has;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                var consenting = await ctx.UserPrivacySettings.AsNoTracking()
                    .Where(p => p.AllowDataCollection)
                    .Select(p => p.UserId)
                    .ToListAsync(stoppingToken);

                snapshot.Replace(consenting);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Deliberately does not clear the snapshot. A failed refresh means "we could not
                // check", and dropping every consent on a transient database blip would silently
                // pseudonymize accounts that did opt in - annoying rather than dangerous, but the
                // last good answer is a better answer than none.
                logger.LogWarning(ex, "Refreshing the data-collection consent snapshot failed");
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
