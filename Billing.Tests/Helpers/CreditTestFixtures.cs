using System.Text.Json;
using Billing.Application.Credit;
using Billing.Application.Services;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Billing.Tests.Helpers;

/// <summary>
/// The pieces every credit test needs: a ledger over a real Postgres, a catalogue with a plan that
/// has a cash price, and the option values the wave's numbers come from.
/// </summary>
internal static class CreditFixtures
{
    public const string Buyer = "user_credit_buyer";
    public const string Guild = "gild_credit_target";
    public const string Staff = "user_credit_staff";

    public const string ProSku = "guild.pro.30d";
    public const string PlusSku = "guild.plus.30d";
    public const string UserPlusSku = "user.plus.30d";

    /// <summary>A SKU with no cash price behind it, for the section 8.1 gate.</summary>
    public const string UnpricedSku = "guild.unpriced.30d";

    public static CreditOptions Options(
        long walletCap = 20_000,
        int lotLifetimeDays = 365,
        int warningDays = 30) => new()
    {
        MaxWalletBalancePoints = walletCap,
        DefaultLotLifetimeDays = lotLifetimeDays,
        ExpiryWarningDays = warningDays,
        Catalogue =
        [
            new CreditSkuOptions
            {
                Code = ProSku,
                Title = "30 days of Pro",
                PricePoints = 500,
                Plan = Plans.Pro,
                DurationDays = 30,
                Subject = Echo.Entitlements.Model.SubjectKind.Guild,
            },
            new CreditSkuOptions
            {
                Code = PlusSku,
                Title = "30 days of Plus",
                PricePoints = 250,
                Plan = Plans.Plus,
                DurationDays = 30,
                Subject = Echo.Entitlements.Model.SubjectKind.Guild,
            },
            new CreditSkuOptions
            {
                Code = UserPlusSku,
                Title = "30 days of Venta Plus",
                PricePoints = 200,
                Plan = Plans.Plus,
                DurationDays = 30,
                Subject = Echo.Entitlements.Model.SubjectKind.User,
            },
            new CreditSkuOptions
            {
                Code = UnpricedSku,
                Title = "Something with no cash route",
                PricePoints = 100,
                Plan = "unpriced",
                DurationDays = 30,
                Subject = Echo.Entitlements.Model.SubjectKind.Guild,
            },
        ],
    };

    public static CreditLedgerService Ledger(
        MicroserviceContext db, TimeProvider clock, CreditOptions? options = null) =>
        new(db, Microsoft.Extensions.Options.Options.Create(options ?? Options()), clock);

    public static CreditCatalogueService Catalogue(MicroserviceContext db, CreditOptions? options = null) =>
        new(db, Microsoft.Extensions.Options.Options.Create(options ?? Options()));

    public static CreditCampaignService Campaigns(MicroserviceContext db, TimeProvider clock) =>
        new(db, clock);

    public static GrantService Grants(MicroserviceContext db, TimeProvider clock) =>
        new(db, new PlanCatalogueService(db, Plans.Catalogue()), new EntitlementVersionService(db), clock);

    public static CreditPurchaseService Purchases(
        MicroserviceContext db, TimeProvider clock, CreditOptions? options = null) =>
        new(db, Ledger(db, clock, options), Catalogue(db, options), Grants(db, clock), clock);

    /// <summary>A plan row with a cash price on its current version.</summary>
    public static async Task SeedPricedPlanAsync(
        MicroserviceContext db,
        string name,
        long? priceMinorUnits = 999,
        string? currency = "eur")
    {
        var now = DateTimeOffset.UtcNow;

        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = name,
            DisplayName = name,
            CurrentVersionNumber = 1,
            CreatedBy = "system",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Plans.Add(plan);

        db.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["voice.max_participants"] = "50",
            }),
            PriceMinorUnits = priceMinorUnits,
            Currency = currency,
            Reason = "Seeded for a test.",
            CreatedBy = "system",
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Sums the raw entries, which is the definition of a balance.</summary>
    public static async Task<long> SumOfEntriesAsync(MicroserviceContext db, string userId)
    {
        var entries = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.CreditEntries.Where(entry => entry.UserId == userId));

        return entries.Sum(entry => entry.Amount);
    }
}
