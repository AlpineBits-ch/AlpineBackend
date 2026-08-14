using System.Globalization;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;

namespace Billing.Application.Credit;

/// <summary>The sentence that has to be next to every balance.</summary>
public static class CreditDisclaimer
{
    public const string Text =
        "Credits are a promotional balance with no cash value. They cannot be bought, refunded, "
        + "transferred or exchanged for money, and each parcel expires on the date shown.";

    /// <summary>A stable key so a client can translate the sentence rather than render the English
    /// one. Both are sent; a client that does not know the key still shows something correct.
    /// </summary>
    public const string LocalisationKey = "credit.disclaimer";
}

/// <summary>One lot, as somebody reads it.</summary>
public sealed record CreditLotDto(
    string LotId,
    long Points,
    long OriginalPoints,
    DateTimeOffset ExpiresAt,
    string? CampaignId)
{
    public static CreditLotDto From(CreditLotRemainder lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        return new CreditLotDto(lot.LotId, lot.Remaining, lot.OriginalAmount, lot.ExpiresAt, lot.CampaignId);
    }
}

/// <summary>A wallet, for the user who owns it or for the admin looking at it.</summary>
public sealed record CreditWalletDto(
    string UserId,
    long Balance,
    long CapPoints,
    IReadOnlyList<CreditLotDto> Lots,
    string Disclaimer,
    string DisclaimerKey,
    DateTimeOffset? CachedAt = null);

/// <summary>One ledger line in plain language.</summary>
public sealed record CreditEntryDto(
    string Id,
    long Amount,
    CreditEntryKind Kind,
    string Summary,
    string? LotId,
    string? CampaignId,
    string? GrantId,
    string? Reason,
    string? CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static CreditEntryDto From(CreditEntry entry, DateTimeOffset? lotExpiry = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new CreditEntryDto(
            entry.Id,
            entry.Amount,
            entry.Kind,
            Describe(entry, lotExpiry),
            entry.LotId,
            entry.CampaignId,
            entry.GrantId,
            entry.Reason,
            entry.CreatedBy,
            entry.CreatedAt);
    }

    private static string Describe(CreditEntry entry, DateTimeOffset? lotExpiry)
    {
        var points = Math.Abs(entry.Amount);
        var credits = points == 1 ? "1 credit" : $"{points} credits";

        return entry.Kind switch
        {
            CreditEntryKind.Issue when lotExpiry is { } expiry =>
                $"{credits} added, expiring {Date(expiry)}",
            CreditEntryKind.Issue => $"{credits} added",
            CreditEntryKind.Spend => $"{credits} spent",
            CreditEntryKind.Expiry => $"{credits} expired",
            CreditEntryKind.Reversal => $"{credits} taken back",
            CreditEntryKind.Adjustment when entry.Amount > 0 => $"{credits} added by an adjustment",
            CreditEntryKind.Adjustment => $"{credits} removed by an adjustment",
            _ => credits,
        };
    }

    /// <summary>Day and month, which is the form section 8.8 asks for ("500 credits, expires 12
    /// March"). Invariant culture because this is a fallback a client may replace; a client that
    /// localises uses the raw <c>ExpiresAt</c> on the lot instead.</summary>
    private static string Date(DateTimeOffset instant) =>
        instant.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>The ledger, newest first, with the balance it adds up to.</summary>
public sealed record CreditLedgerDto(
    string UserId,
    long Balance,
    IReadOnlyList<CreditEntryDto> Entries,
    string Disclaimer,
    string DisclaimerKey);

/// <summary>One thing credit buys.</summary>
public sealed record CreditSkuDto(
    string Code,
    string Title,
    string? Description,
    long PricePoints,
    long? CashPriceMinorUnits,
    string? CashCurrency,
    string Plan,
    int DurationDays,
    SubjectKind Subject)
{
    public static CreditSkuDto From(CreditSku sku)
    {
        ArgumentNullException.ThrowIfNull(sku);

        return new CreditSkuDto(
            sku.Code, sku.Title, sku.Description, sku.PricePoints,
            sku.CashPriceMinorUnits, sku.CashCurrency, sku.Plan, sku.DurationDays, sku.Subject);
    }
}

/// <summary>What the balance can buy right now, with the balance beside it so a client does not have
/// to make two calls to grey out what it cannot afford.</summary>
public sealed record CreditCatalogueDto(
    long Balance,
    IReadOnlyList<CreditSkuDto> Skus,
    string Disclaimer,
    string DisclaimerKey);

/// <summary>What a purchase gives back.</summary>
public sealed record CreditPurchaseDto(
    string SkuCode,
    string GrantId,
    SubjectKind SubjectKind,
    string SubjectId,
    long Spent,
    long BalanceAfter,
    DateTimeOffset StartsAt,
    DateTimeOffset? ExpiresAt,
    bool WasQueued)
{
    public static CreditPurchaseDto From(CreditPurchase purchase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        return new CreditPurchaseDto(
            purchase.Sku.Code,
            purchase.Grant.Id,
            purchase.Grant.SubjectKind,
            purchase.Grant.SubjectId,
            purchase.Sku.PricePoints,
            purchase.BalanceAfter,
            purchase.Grant.StartsAt ?? now,
            purchase.Grant.ExpiresAt,
            purchase.WasQueued);
    }
}

/// <summary>A campaign and how much of its budget is gone, which is the one number section 8.6 asks
/// the console to show.</summary>
public sealed record CreditCampaignDto(
    string Id,
    string Code,
    string Description,
    long TotalBudgetPoints,
    long IssuedPoints,
    long RemainingPoints,
    long? MaxPerUserPoints,
    int AlertThresholdPercent,
    DateTimeOffset? AlertedAt,
    bool Automated,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? PausedAt,
    string? PausedBy,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static CreditCampaignDto From(CreditCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return new CreditCampaignDto(
            campaign.Id,
            campaign.Code,
            campaign.Description,
            campaign.TotalBudgetPoints,
            campaign.IssuedPoints,
            campaign.RemainingPoints,
            campaign.MaxPerUserPoints,
            campaign.AlertThresholdPercent,
            campaign.AlertedAt,
            campaign.Automated,
            campaign.StartsAt,
            campaign.EndsAt,
            campaign.PausedAt,
            campaign.PausedBy,
            campaign.CreatedBy,
            campaign.CreatedAt);
    }
}

/// <summary>What the console posts to hand somebody credit.</summary>
public sealed record IssueCreditRequest(
    long Amount,
    string Reason,
    string IdempotencyKey,
    string? Campaign = null,
    DateTimeOffset? ExpiresAt = null,
    int? LifetimeDays = null);

public sealed record AdjustCreditRequest(long Amount, string Reason, string IdempotencyKey);

public sealed record ReverseCreditRequest(string Reason);

/// <summary>Emptying a wallet after a fraud ban.</summary>
public sealed record VoidCreditRequest(string Reason);

public sealed record PurchaseCreditRequest(string Sku, string? TargetId, string IdempotencyKey);

public sealed record CreateCreditCampaignRequest(
    string Code,
    string Description,
    long TotalBudgetPoints,
    long? MaxPerUserPoints = null,
    long? DefaultIssuePoints = null,
    int? LotLifetimeDays = null,
    int AlertThresholdPercent = 80,
    bool Automated = false,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record SetCreditBudgetRequest(long TotalBudgetPoints);

/// <summary>A refusal a console can switch on.</summary>
public sealed record CreditErrorDto(string Code, string Message);

/// <summary>The result of a ledger write, for the console.</summary>
public sealed record CreditWriteDto(
    long Balance,
    IReadOnlyList<CreditEntryDto> Entries,
    bool WasReplay);
