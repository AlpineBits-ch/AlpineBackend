using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>
/// Adds entitlement keys that a configured plan gained after that plan was already seeded.
/// <see cref="PlanSeeder"/> returns on the first row in the table, so a key added to
/// <c>Entitlements:Plans</c> later never reaches a database that has already started once: it
/// resolves to its catalogue default instead, which for a flag is false.
/// </summary>
public static class PlanEntitlementBackfill
{
    public const string Reason =
        "Entitlement keys added to the configured plan after this plan was seeded. Values already "
        + "stored were left alone.";

    /// <summary>Returns how many plans gained a new version, which is zero once each configured
    /// key exists on its plan.</summary>
    public static async Task<int> RunAsync(
        MicroserviceContext db,
        PlanCatalogue configured,
        PlanService plans,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(plans);

        var wanted = ConfiguredValuesByPlan(configured, logger);
        if (wanted.Count == 0) return 0;

        // Only plans this service planted. An operator-created plan omitting a key is a decision.
        var seeded = await db.Plans
            .Where(plan => plan.SeededFromConfiguration && plan.ArchivedAt == null)
            .ToListAsync(cancellationToken);

        var filled = 0;

        foreach (var plan in seeded)
        {
            if (!wanted.TryGetValue(plan.Name, out var configuredValues)) continue;

            var current = await db.PlanVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    version => version.PlanId == plan.Id
                               && version.VersionNumber == plan.CurrentVersionNumber,
                    cancellationToken);

            if (current is null) continue;

            var stored = PlanCatalogueService.ReadValues(current.ValuesJson);
            var missing = configuredValues
                .Where(pair => !stored.ContainsKey(pair.Key))
                .ToList();

            if (missing.Count == 0) continue;

            var merged = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in missing) merged[key] = value;

            try
            {
                await plans.EditAsync(
                    plan.Name,
                    new EditPlan(merged, current.PriceMinorUnits, current.Currency, Reason),
                    PlanSeeder.SystemActor,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // One unbackfillable plan must not stop the service starting, or a Stripe outage
                // becomes a boot loop.
                logger?.LogError(ex,
                    "Could not backfill {Count} entitlement key(s) onto plan {Plan}: {Keys}",
                    missing.Count, plan.Name, string.Join(", ", missing.Select(pair => pair.Key)));
                continue;
            }

            filled++;
            logger?.LogWarning(
                "Plan {Plan} was missing {Count} configured entitlement key(s) and gained them in a "
                + "new version: {Keys}. Until now they resolved to their catalogue defaults.",
                plan.Name, missing.Count, string.Join(", ", missing.Select(pair => pair.Key)));
        }

        return filled;
    }

    private static Dictionary<string, Dictionary<string, string>> ConfiguredValuesByPlan(
        PlanCatalogue configured, ILogger? logger)
    {
        var byPlan = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in configured.Plans)
        {
            string name;
            try
            {
                name = PlanService.NormaliseName(definition.Name);
            }
            catch (PlanRefusedException refusal)
            {
                logger?.LogWarning("Skipping configured plan '{Plan}' during backfill: {Reason}",
                    definition.Name, refusal.Message);
                continue;
            }

            byPlan[name] = definition.Values.ToDictionary(
                value => value.Key.Name,
                value => value.Key.Format(value.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        return byPlan;
    }
}
