using System.Text.Json;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>The parts of a plan that are not entitlement values: what it is called and what it
/// costs. Separate from <c>Entitlements:Plans</c> because that section is bound by every service
/// that resolves entitlements and a price is none of their business.</summary>
public sealed class PlanSeedEntry
{
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>Minor units - cents - matching Stripe, so the price object this eventually becomes
    /// needs no conversion.</summary>
    public long? PriceMinorUnits { get; set; }

    public string? Currency { get; set; }
}

public sealed class PlanSeedOptions
{
    public const string SectionName = "Billing";

    public Dictionary<string, PlanSeedEntry> PlanSeed { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Plants the configured plans in the database the first time the service starts against an empty
/// catalogue.
/// </summary>
public static class PlanSeeder
{
    /// <summary>What the audit trail records as the actor.</summary>
    public const string SystemActor = "system";

    public const string SeedReason =
        "Seeded from configuration on first start. These are the pricing model's proposed defaults, "
        + "not decided prices, and the owner is expected to edit them from the admin console.";

    /// <summary>Returns how many plans were planted, which is zero on every start after the first.
    /// </summary>
    public static async Task<int> SeedAsync(
        MicroserviceContext db,
        PlanCatalogue configured,
        PlanSeedOptions seed,
        TimeProvider clock,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(clock);

        if (await db.Plans.AnyAsync(cancellationToken)) return 0;

        var definitions = configured.Plans.ToList();
        if (definitions.Count == 0)
        {
            // A legitimate state, and the one every deployment was in until the pricing numbers
            // landed: no plans configured means no plans, and every key resolves to its catalogue
            // default. The console can create them by hand.
            logger?.LogInformation(
                "No plans are configured, so the plan catalogue starts empty. Entitlement keys "
                + "resolve to their catalogue defaults until a plan exists.");
            return 0;
        }

        var now = clock.GetUtcNow();

        var planted = 0;

        foreach (var definition in definitions)
        {
            string name;
            try
            {
                name = PlanService.NormaliseName(definition.Name);
            }
            catch (PlanRefusedException refusal)
            {
                // A configuration key that could not be a plan name.
                logger?.LogWarning("Skipping configured plan '{Plan}': {Reason}",
                    definition.Name, refusal.Message);
                continue;
            }

            var entry = seed.PlanSeed.GetValueOrDefault(definition.Name);

            var plan = new Plan
            {
                Id = Plan.GenerateId(),
                Name = name,
                DisplayName = entry?.DisplayName,
                Description = entry?.Description,
                CurrentVersionNumber = 1,
                SeededFromConfiguration = true,
                CreatedBy = SystemActor,
            };

            db.Plans.Add(plan);

            db.PlanVersions.Add(new PlanVersion
            {
                Id = PlanVersion.GenerateId(),
                PlanId = plan.Id,
                VersionNumber = 1,
                ValuesJson = JsonSerializer.Serialize(definition.Values.ToDictionary(
                    value => value.Key.Name, value => value.Key.Format(value.Value))),
                PriceMinorUnits = entry?.PriceMinorUnits,
                Currency = string.IsNullOrWhiteSpace(entry?.Currency)
                    ? null
                    : entry.Currency.Trim().ToLowerInvariant(),
                Reason = SeedReason,
                CreatedBy = SystemActor,
            });

            db.PlanAuditEntries.Add(new PlanAuditEntry
            {
                Id = PlanAuditEntry.GenerateId(),
                PlanId = plan.Id,
                VersionNumber = 1,
                Action = PlanChangeAction.PlanCreated,
                Actor = SystemActor,
                Reason = SeedReason,
                AffectedSubjects = 0,
                OccurredAt = now,
            });

            planted++;
        }

        // Not a Wolverine handler, so nothing commits this for us.
        await db.SaveChangesAsync(cancellationToken);

        logger?.LogInformation(
            "Planted {Count} plan(s) from configuration: {Plans}. They are the pricing model's "
            + "proposed defaults and are expected to be edited from the admin console.",
            planted, string.Join(", ", definitions.Select(plan => plan.Name)));

        return planted;
    }
}
