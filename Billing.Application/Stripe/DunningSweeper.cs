using AppEnvironment;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Billing.Application.Stripe;

/// <summary>
/// Performs the downgrades whose trigger is a date passing rather than a delivery.
/// </summary>
public class DunningSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<DunningSweeper> logger) : BackgroundService
{
    /// <summary>Longer than <c>GrantExpirySweeper</c>'s five minutes, because every candidate costs a
    /// Stripe API call and a grace period is measured in days: being a quarter of an hour late to end
    /// one is not a difference anybody can perceive.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Bounded so a backlog drains over several passes instead of becoming one burst of
    /// Stripe calls, matching the other sweeps in this codebase.</summary>
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Dunning sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // An instance with no Stripe key has nothing to read the live state from, and the
        // reconciler would throw on the first call.
        if (!Env.License.IsStripeConfigured) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var reconciler = scope.ServiceProvider.GetRequiredService<SubscriptionReconciler>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var announcements = await CollectAsync(db, reconciler, clock.GetUtcNow(), cancellationToken);

        if (announcements.Count == 0) return;

        // Explicit, unlike every Wolverine handler in this service.
        await db.SaveChangesAsync(cancellationToken);

        foreach (var announcement in announcements)
        {
            await bus.PublishAsync(announcement);
        }

        logger.LogInformation(
            "Dunning sweep moved {Subjects} subject(s) off a subscription whose grace period had run out",
            announcements.Count);
    }

    /// <summary>The candidates, reconciled, and the events that would go out.</summary>
    internal static async Task<IReadOnlyList<EntitlementsChanged>> CollectAsync(
        MicroserviceContext db,
        SubscriptionReconciler reconciler,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(reconciler);

        // Only past_due, because that is the only status the grace period changes the answer for.
        var elapsed = await db.Subscriptions
            .Where(row => row.Status == SubscriptionStatus.PastDue
                          && row.GracePeriodEndsAt != null
                          && row.GracePeriodEndsAt <= now)
            .OrderBy(row => row.GracePeriodEndsAt)
            .Take(BatchSize)
            .Select(row => row.StripeSubscriptionId)
            .ToListAsync(cancellationToken);

        var announcements = new List<EntitlementsChanged>();

        foreach (var subscriptionId in elapsed)
        {
            var reconciliation = await reconciler.ReconcileAsync(
                subscriptionId, StripeDunningSignal.None, "dunning.grace_elapsed", now, cancellationToken);

            announcements.AddRange(reconciliation.Announcements);
        }

        return announcements;
    }
}
