using AppEnvironment;
using Billing.Application.Stripe;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Billing.Application.Credit;

/// <summary>Spends credit on a renewal before the card is asked for it.</summary>
public class CreditRenewalSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<CreditRenewalSweeper> logger) : BackgroundService
{
    /// <summary>Matching <c>DunningSweeper</c> rather than the credit expiry sweep's hour: every
    /// candidate costs a Stripe call, and the deadline this is racing is days away. Being a quarter of
    /// an hour late to buy a renewal is not a difference anybody can perceive.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Bounded so a backlog drains over several passes instead of becoming one burst of
    /// Stripe calls, matching the other sweeps in this codebase.</summary>
    private const int BatchSize = 100;

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
                logger.LogError(exception, "Credit renewal sweep failed");
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
        // No Stripe key means nothing to tell to stand down, and buying the grant on its own would
        // give the time away for free.
        if (!Env.License.IsStripeConfigured) return;

        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var announcements = await CollectAsync(
            provider.GetRequiredService<MicroserviceContext>(),
            provider.GetRequiredService<CreditLedgerService>(),
            provider.GetRequiredService<CreditCatalogueService>(),
            provider.GetRequiredService<CreditPurchaseService>(),
            provider.GetRequiredService<IStripeGateway>(),
            provider.GetRequiredService<TimeProvider>().GetUtcNow(),
            provider.GetRequiredService<IOptions<CreditOptions>>().Value,
            BatchSize,
            logger,
            cancellationToken);

        if (announcements.Count == 0) return;

        var bus = provider.GetRequiredService<IMessageBus>();

        foreach (var announcement in announcements)
        {
            await bus.PublishAsync(announcement);
        }
    }

    /// <summary>
    /// Buys the renewals that are due and returns the entitlement changes they produced.
    /// </summary>
    internal static async Task<IReadOnlyList<EntitlementsChanged>> CollectAsync(
        MicroserviceContext db,
        CreditLedgerService ledger,
        CreditCatalogueService catalogue,
        CreditPurchaseService purchases,
        IStripeGateway gateway,
        DateTimeOffset now,
        CreditOptions options,
        int batchSize,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);

        // The operator's half of the consent, read on every pass rather than at registration so
        // that turning it off is a configuration reload rather than a redeploy.
        if (!options.RenewFromCredit) return [];

        var skus = await catalogue.PurchasableAsync(cancellationToken);
        if (skus.Count == 0) return [];

        var due = await DueAsync(db, now, options, batchSize, cancellationToken);
        if (due.Count == 0) return [];

        var announcements = new List<EntitlementsChanged>();

        foreach (var subscription in due)
        {
            try
            {
                var announcement = await RenewAsync(
                    db, ledger, purchases, gateway, skus, subscription, now, logger, cancellationToken);

                if (announcement is not null) announcements.Add(announcement);
            }
            catch (Exception exception)
            {
                // One subscription's problem is not the batch's.
                logger?.LogError(
                    exception,
                    "Could not renew subscription {Subscription} from credit; it will be charged as normal",
                    subscription.StripeSubscriptionId);
            }
        }

        return announcements;
    }

    /// <summary>The subscriptions whose next charge is close enough to buy.</summary>
    private static Task<List<Subscription>> DueAsync(
        MicroserviceContext db,
        DateTimeOffset now,
        CreditOptions options,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var horizon = now.AddDays(Math.Max(0, options.RenewalLeadDays));

        return db.Subscriptions
            .Where(row => row.UseCreditBeforeCharging
                          && !row.CancelAtPeriodEnd
                          && (row.Status == SubscriptionStatus.Active
                              || row.Status == SubscriptionStatus.Trialing)
                          && row.CurrentPeriodEnd != null
                          // In the future, because a period end that has already passed while the
                          // status is still active means this row is behind Stripe rather than that
                          // a renewal is due.
                          && row.CurrentPeriodEnd > now
                          && row.CurrentPeriodEnd <= horizon)
            .OrderBy(row => row.CurrentPeriodEnd)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>One renewal: pick the SKU, spend, tell Stripe to stand down, commit.</summary>
    private static async Task<EntitlementsChanged?> RenewAsync(
        MicroserviceContext db,
        CreditLedgerService ledger,
        CreditPurchaseService purchases,
        IStripeGateway gateway,
        IReadOnlyList<CreditSku> skus,
        Subscription subscription,
        DateTimeOffset now,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var periodEnd = subscription.CurrentPeriodEnd!.Value;

        var plan = await db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == subscription.PlanId, cancellationToken);

        if (plan is null) return null;

        // A user-plan subscription is always paid for by the account it is on, and a user SKU
        // always lands on the buyer.
        if (subscription.SubjectKind == SubjectKind.User
            && !string.Equals(subscription.SubjectId, subscription.PayerUserId, StringComparison.Ordinal))
        {
            return null;
        }

        var balance = await ledger.BalanceAsync(subscription.PayerUserId, cancellationToken);

        var sku = Best(skus, plan.Name, subscription.SubjectKind, balance);
        if (sku is null) return null;

        // The period end is in the key, so a second pass before Stripe's new period has mirrored back
        // re-derives the same one and replays the first purchase instead of buying a second month.
        var idempotencyKey = $"renewal:{subscription.StripeSubscriptionId}:{periodEnd:O}";

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var purchase = await purchases.PurchaseAsync(
            subscription.PayerUserId,
            sku.Code,
            subscription.SubjectId,
            idempotencyKey,
            cancellationToken,
            startNotBefore: periodEnd);

        var resumeAt = purchase.Grant.ExpiresAt;

        // Unreachable for a catalogue SKU, which cannot have a null duration and cannot be
        // permanent.
        if (resumeAt is not { } resume || resume <= now) return null;

        var snapshot = await gateway.DeferBillingAsync(
            subscription.StripeSubscriptionId,
            resume,
            StripeIdempotencyKey.ForRequest("defer_billing", subscription.StripeSubscriptionId),
            cancellationToken);

        // Mirrored inside the same transaction so the next pass reads the new period end and finds
        // this subscription outside its horizon.
        subscription.MirrorFromStripe(
            SubscriptionStatuses.Parse(snapshot.Status),
            snapshot.CurrentPeriodEnd,
            snapshot.CancelAtPeriodEnd,
            snapshot.LatestInvoiceId);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger?.LogInformation(
            "Renewed {Subscription} from credit: {Payer} spent {Points} on '{Sku}' for {SubjectKind} "
            + "{SubjectId}, and the card stands down until {ResumeAt}",
            subscription.StripeSubscriptionId, subscription.PayerUserId, sku.PricePoints, sku.Code,
            subscription.SubjectKind, subscription.SubjectId, resume.ToString("O"));

        return purchase.Announcement;
    }

    /// <summary>Which SKU renews this subscription, if any.</summary>
    internal static CreditSku? Best(
        IReadOnlyList<CreditSku> skus, string planName, SubjectKind subject, long balance)
    {
        ArgumentNullException.ThrowIfNull(skus);

        return skus
            .Where(sku => sku.Subject == subject
                          && string.Equals(sku.Plan, planName, StringComparison.OrdinalIgnoreCase)
                          && sku.PricePoints <= balance)
            .OrderByDescending(sku => sku.DurationDays)
            .ThenBy(sku => sku.PricePoints)
            .FirstOrDefault();
    }
}
