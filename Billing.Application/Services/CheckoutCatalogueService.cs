using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>Whose plan a plan is.</summary>
public static class PlanSubjectKinds
{
    public static SubjectKind Of(string planName, IEnumerable<string> keyNames, string? defaultUserPlan)
    {
        ArgumentNullException.ThrowIfNull(keyNames);

        var guild = false;
        var user = false;

        foreach (var name in keyNames)
        {
            if (!EntitlementKeys.TryGet(name, out var key)) continue;

            if (key.Scope == EntitlementScope.Guild) guild = true;
            if (key.Scope == EntitlementScope.User) user = true;
        }

        if (guild) return SubjectKind.Guild;
        if (user) return SubjectKind.User;

        return string.Equals(defaultUserPlan, planName, StringComparison.OrdinalIgnoreCase)
            ? SubjectKind.User
            : SubjectKind.Guild;
    }
}

/// <summary>What is for sale, for the customer who is about to buy it.</summary>
public sealed class CheckoutCatalogueService(
    MicroserviceContext db,
    EntitlementPlanOptions options,
    ILogger<CheckoutCatalogueService>? logger = null)
{
    /// <summary>The currency the envelope falls back to when no plan carries one, which happens only
    /// on an instance whose plans are all free. Nothing is priced in it; it exists so the client has
    /// a code to format a zero with rather than a null to branch on.</summary>
    public const string FallbackCurrency = "usd";

    public async Task<CatalogueDto> ReadAsync(CancellationToken cancellationToken)
    {
        var plans = await db.Plans
            .AsNoTracking()
            .Where(plan => plan.ArchivedAt == null)
            .ToListAsync(cancellationToken);

        var planIds = plans.Select(plan => plan.Id).ToList();

        var versions = await db.PlanVersions
            .AsNoTracking()
            .Where(version => planIds.Contains(version.PlanId) && version.ArchivedAt == null)
            .ToListAsync(cancellationToken);

        var ladders = new Dictionary<string, IReadOnlyList<EntitlementRungDto>>(StringComparer.Ordinal);
        var rows = new List<CataloguePlanDto>(plans.Count);

        foreach (var plan in plans)
        {
            var version = versions.FirstOrDefault(
                candidate => candidate.PlanId == plan.Id
                             && candidate.VersionNumber == plan.CurrentVersionNumber);

            // A plan whose current version is archived publishes nothing.
            if (version is null) continue;

            var values = ParseValues(plan, version);

            var kind = PlanSubjectKinds.Of(
                plan.Name, values.Keys.Select(key => key.Name), options.DefaultUserPlan);

            var entitlements = new Dictionary<string, EntitlementValueDto>(StringComparer.Ordinal);

            foreach (var (key, value) in values)
            {
                // A guild plan that mentions a user key is harmless rather than surprising - the
                // resolver drops it for the same reason - but publishing it would put a row in the
                // comparison table that nobody buying this plan will ever get.
                if (!key.AppliesTo(kind)) continue;

                entitlements[key.Name] = EntitlementValueDto.From(key, value);

                if (key.Ladder is not null && !ladders.ContainsKey(key.Ladder.Name))
                {
                    ladders[key.Ladder.Name] = EntitlementRungDto.Describe(key.Ladder);
                }
            }

            var currency = string.IsNullOrWhiteSpace(version.Currency)
                ? null
                : version.Currency.Trim().ToLowerInvariant();

            rows.Add(new CataloguePlanDto(
                plan.Name,
                plan.DisplayName ?? plan.Name,
                plan.Description,
                version.VersionNumber,
                kind,
                version.PriceMinorUnits,
                currency,

                // Monthly only in this wave, and stated rather than implied: the client formats
                // "$29 / month" from it, and an absent interval is a client inventing one.
                StripeCatalogueSyncInterval,
                version.PriceMinorUnits is > 0 && currency is not null,
                entitlements));
        }

        // Guild plans first, then user plans, each cheapest first, so the comparison table reads in
        // the order somebody upgrades through without the client sorting on a price it may not have.
        rows.Sort((left, right) =>
        {
            var byKind = left.SubjectKind.CompareTo(right.SubjectKind);
            if (byKind != 0) return byKind;

            var byPrice = (left.PriceMinorUnits ?? 0).CompareTo(right.PriceMinorUnits ?? 0);
            return byPrice != 0 ? byPrice : string.CompareOrdinal(left.Name, right.Name);
        });

        return new CatalogueDto(
            Env.License.IsStripeConfigured,
            rows.FirstOrDefault(row => row.Currency is not null)?.Currency ?? FallbackCurrency,
            rows,
            ladders);
    }

    /// <summary>Mirrors <c>StripeCatalogueSync.MonthlyInterval</c>.</summary>
    private const string StripeCatalogueSyncInterval = "month";

    /// <summary>Skips a key it cannot read rather than throwing, the same rule
    /// <see cref="PlanCatalogueService"/> reads by: a key removed from the catalogue must not take the
    /// whole shop window down with it.</summary>
    private Dictionary<EntitlementKey, EntitlementValue> ParseValues(Plan plan, PlanVersion version)
    {
        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();

        foreach (var (name, text) in PlanCatalogueService.ReadValues(version.ValuesJson))
        {
            if (!EntitlementKeys.TryGet(name, out var key))
            {
                logger?.LogWarning(
                    "Plan '{Plan}' version {Version} names entitlement key '{Key}', which is not in "
                    + "the catalogue. Leaving it off the checkout catalogue.",
                    plan.Name, version.VersionNumber, name);
                continue;
            }

            try
            {
                parsed[key] = key.Parse(text ?? string.Empty);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                logger?.LogWarning(exception,
                    "Plan '{Plan}' version {Version} sets '{Key}' to a value it cannot take. Leaving "
                    + "it off the checkout catalogue.", plan.Name, version.VersionNumber, name);
            }
        }

        return parsed;
    }
}
