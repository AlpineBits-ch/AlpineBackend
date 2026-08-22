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

    /// <summary>Returns how many plans were amended, which is zero once each configured key exists
    /// on every live version of its plan. Idempotent, so the second replica finds nothing to do.
    /// </summary>
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

            try
            {
                var keys = await plans.BackfillMissingValuesAsync(
                    plan.Name, configuredValues, Reason, PlanSeeder.SystemActor, cancellationToken);

                if (keys.Count == 0) continue;

                // PlanService is written for Wolverine handlers, where the middleware commits. This
                // runs at startup, so nothing else will. Per plan, so one failure keeps the rest.
                await db.SaveChangesAsync(cancellationToken);
                filled++;
            }
            catch (Exception ex)
            {
                // One unbackfillable plan must not stop the service starting.
                logger?.LogError(ex, "Could not backfill entitlement keys onto plan {Plan}", plan.Name);

                // A half-applied amendment must not ride along with the next plan's save.
                db.ChangeTracker.Clear();
            }
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
