using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billing.Application.Credit;

/// <summary>What credit can be spent on, with the cash price of each thing beside it.</summary>
public sealed class CreditCatalogueService(MicroserviceContext db, IOptions<CreditOptions> options)
{
    private readonly CreditOptions _options = options?.Value ?? new CreditOptions();

    public CreditOptions Options => _options;

    /// <summary>Every configured SKU, priced.</summary>
    public async Task<IReadOnlyList<CreditSku>> AllAsync(CancellationToken cancellationToken)
    {
        var configured = _options.Catalogue
            .Where(sku => !string.IsNullOrWhiteSpace(sku.Code) && !string.IsNullOrWhiteSpace(sku.Plan))
            .ToList();

        if (configured.Count == 0) return [];

        var planNames = configured.Select(sku => sku.Plan).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The current version of each named plan, and what it costs in money.
        var prices = await db.Plans
            .AsNoTracking()
            .Where(plan => planNames.Contains(plan.Name) && plan.ArchivedAt == null)
            .Select(plan => new
            {
                plan.Name,
                Version = db.PlanVersions
                    .Where(version => version.PlanId == plan.Id
                                      && version.VersionNumber == plan.CurrentVersionNumber)
                    .Select(version => new { version.PriceMinorUnits, version.Currency })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var priceByPlan = prices.ToDictionary(
            row => row.Name,
            row => (row.Version?.PriceMinorUnits, row.Version?.Currency),
            StringComparer.OrdinalIgnoreCase);

        return configured.Select(sku =>
        {
            priceByPlan.TryGetValue(sku.Plan, out var price);

            return new CreditSku(
                sku.Code.Trim(),
                sku.Title,
                sku.Description,
                sku.PricePoints,
                sku.Plan,
                sku.DurationDays,
                sku.Subject,
                price.Item1,
                price.Item2);
        }).ToList();
    }

    /// <summary>The SKUs a user may actually buy: priced in points, priced in cash, and lasting more
    /// than zero days.</summary>
    public async Task<IReadOnlyList<CreditSku>> PurchasableAsync(CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);
        return all.Where(IsPurchasable).ToList();
    }

    public async Task<CreditSku?> FindAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var all = await AllAsync(cancellationToken);
        return all.FirstOrDefault(sku => string.Equals(sku.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The three things that have to be true, in one place, so the catalogue screen and the
    /// purchase path cannot disagree about what is on sale.</summary>
    public static bool IsPurchasable(CreditSku sku)
    {
        ArgumentNullException.ThrowIfNull(sku);

        return sku.PricePoints > 0 && sku.DurationDays > 0 && sku.HasCashPrice;
    }
}
