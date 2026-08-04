using System.Collections.Concurrent;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>
/// The set of accounts that have consented to being identified in telemetry, kept in memory so that
/// <c>AppEnvironment.SentryPrivacy.HasDataCollectionConsent</c> - a synchronous delegate called
/// from inside the Sentry SDK's <c>BeforeSend</c> - can be answered without a database round trip.
/// </summary>
public class DataCollectionConsentSnapshot
{
    private readonly ConcurrentDictionary<string, byte> _consented = new();

    /// <summary>True only for an account known to have opted in.</summary>
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
    /// <summary>Convergence interval.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Installed before the first refresh, and safe to: the snapshot is empty, so every answer
        // is "no consent" until it has actually been loaded.
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
                // Deliberately does not clear the snapshot.
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
