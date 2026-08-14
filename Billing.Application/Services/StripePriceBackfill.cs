using AppEnvironment;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>
/// Gives every priced plan version that has no Stripe price one, once, at startup.
/// </summary>
public static class StripePriceBackfill
{
    /// <summary>Returns how many versions were mirrored, which is zero on every start after the
    /// first one that had anything to do.</summary>
    public static async Task<int> RunAsync(
        MicroserviceContext db,
        StripeCatalogueSync sync,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sync);

        // The same gate every other Stripe surface asks, in the same words.
        if (!Env.License.IsStripeConfigured) return 0;

        var pending = await db.PlanVersions
            .Where(version => version.ArchivedAt == null
                              && version.StripePriceId == null
                              && version.PriceMinorUnits != null
                              && version.PriceMinorUnits > 0)
            .OrderBy(version => version.PlanId)
            .ThenBy(version => version.VersionNumber)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return 0;

        var planIds = pending.Select(version => version.PlanId).Distinct().ToList();

        var plans = await db.Plans
            .Where(plan => planIds.Contains(plan.Id) && plan.ArchivedAt == null)
            .ToDictionaryAsync(plan => plan.Id, cancellationToken);

        var mirrored = 0;

        foreach (var version in pending)
        {
            // A version of an archived plan is not sold, so mirroring it would create a Stripe price
            // for something the catalogue no longer publishes.
            if (!plans.TryGetValue(version.PlanId, out var plan)) continue;

            try
            {
                if (await sync.SyncAsync(plan, version, cancellationToken) == StripeSyncOutcome.Synced)
                {
                    mirrored++;
                }
            }
            catch (StripeGatewayException failure)
            {
                logger?.LogError(failure,
                    "Version {Version} of plan '{Plan}' could not be mirrored into Stripe and stays "
                    + "unbuyable until the next start. Nothing else is affected; the idempotency key "
                    + "makes the retry free.", version.VersionNumber, plan.Name);
            }
        }

        // Not a Wolverine handler, so nothing commits this for us.
        if (mirrored > 0) await db.SaveChangesAsync(cancellationToken);

        logger?.LogInformation(
            "Backfilled {Mirrored} of {Pending} priced plan version(s) into Stripe. Plans seeded "
            + "before the checkout surface existed had a price and no Stripe price, which renders a "
            + "catalogue nobody can buy from.", mirrored, pending.Count);

        return mirrored;
    }
}
