using AppEnvironment;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Stripe;

/// <summary>What a sync attempt did, so a caller can log the difference between "nothing to do" and
/// "done" without inferring it from the ids.</summary>
public enum StripeSyncOutcome
{
    /// <summary>No Stripe key on this instance.</summary>
    NotConfigured,

    /// <summary>The version has no price.</summary>
    NotPriced,

    /// <summary>Already mirrored.</summary>
    AlreadySynced,

    Synced,
}

/// <summary>The plan and version a Stripe price id belongs to.</summary>
public sealed record PlanVersionReference(Plan Plan, PlanVersion Version);

/// <summary>
/// Mirrors a priced <see cref="PlanVersion"/> into Stripe as a Price under the plan's Product, and
/// resolves the mapping back again.
/// </summary>
public sealed class StripeCatalogueSync(
    MicroserviceContext db,
    IStripeGateway gateway,
    ILogger<StripeCatalogueSync>? logger = null)
{
    /// <summary>Monthly only in this wave.</summary>
    public const string MonthlyInterval = "month";

    /// <summary>The plan's name, so a person reading the Stripe dashboard sees <c>pro</c> rather than
    /// only a display name that is allowed to change.</summary>
    public const string PlanMetadataKey = "venta_plan";

    /// <summary>The plan's own id, which is what actually survives a rename and is what
    /// <see cref="StripeIdempotencyKey.ForProduct"/> is keyed on.</summary>
    public const string PlanIdMetadataKey = "venta_plan_id";

    public const string PlanVersionMetadataKey = "venta_plan_version";

    /// <summary>
    /// Ensures the plan has a Stripe Product and this version has a Stripe Price, writing both ids
    /// back onto the entities.
    /// </summary>
    public async Task<StripeSyncOutcome> SyncAsync(
        Plan plan, PlanVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(version);

        if (!Env.License.IsStripeConfigured) return StripeSyncOutcome.NotConfigured;

        var currency = version.Currency?.Trim().ToLowerInvariant();

        if (version.PriceMinorUnits is not > 0 || string.IsNullOrWhiteSpace(currency))
        {
            return StripeSyncOutcome.NotPriced;
        }

        if (!string.IsNullOrWhiteSpace(version.StripePriceId)) return StripeSyncOutcome.AlreadySynced;

        if (string.IsNullOrWhiteSpace(plan.StripeProductId))
        {
            var product = await gateway.CreateProductAsync(
                new StripeProductRequest(
                    plan.DisplayName ?? plan.Name,
                    plan.Description,
                    new Dictionary<string, string>
                    {
                        [PlanMetadataKey] = plan.Name,
                        [PlanIdMetadataKey] = plan.Id,
                    }),
                StripeIdempotencyKey.ForProduct(plan.Id),
                cancellationToken);

            plan.StripeProductId = product.Id;
        }

        var price = await gateway.CreatePriceAsync(
            new StripePriceRequest(
                plan.StripeProductId!,
                version.PriceMinorUnits.Value,
                currency,
                MonthlyInterval,

                // Both directions of identity.
                new Dictionary<string, string>
                {
                    [PlanMetadataKey] = plan.Name,
                    [PlanIdMetadataKey] = plan.Id,
                    [PlanVersionMetadataKey] = version.VersionNumber.ToString(),
                }),
            StripeIdempotencyKey.ForPrice(plan.Id, version.VersionNumber, currency, MonthlyInterval),
            cancellationToken);

        version.StripePriceId = price.Id;

        logger?.LogInformation(
            "Mirrored plan '{Plan}' version {Version} into Stripe as price {Price} on product "
            + "{Product}.", plan.Name, version.VersionNumber, price.Id, plan.StripeProductId);

        return StripeSyncOutcome.Synced;
    }

    /// <summary>The reverse: which plan and version a Stripe price id was created for.</summary>
    public async Task<PlanVersionReference?> ResolvePriceAsync(
        string stripePriceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stripePriceId)) return null;

        var version = await db.PlanVersions
            .FirstOrDefaultAsync(candidate => candidate.StripePriceId == stripePriceId, cancellationToken);

        if (version is null) return null;

        var plan = await db.Plans
            .FirstOrDefaultAsync(candidate => candidate.Id == version.PlanId, cancellationToken);

        return plan is null ? null : new PlanVersionReference(plan, version);
    }
}
