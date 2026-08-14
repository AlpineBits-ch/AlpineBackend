using Echo.Entitlements.Model;

namespace Billing.Application.Credit;

/// <summary>What one SKU may be.</summary>
public enum CreditSkuKind
{
    /// <summary>A plan, for a fixed number of days, on a guild or on the buyer.</summary>
    TimeBoxedPlanGrant,
}

/// <summary>One catalogue entry, as configuration binds it.</summary>
public sealed class CreditSkuOptions
{
    /// <summary>The stable key a purchase names, such as <c>guild.pro.30d</c>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>What the user-facing catalogue calls it.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The price in points.</summary>
    public long PricePoints { get; set; }

    /// <summary>The plan the grant names, matching <c>Plan.Name</c>.</summary>
    public string Plan { get; set; } = string.Empty;

    public int DurationDays { get; set; }

    /// <summary>Whether this SKU is applied to a guild or to the buyer's own account.</summary>
    public SubjectKind Subject { get; set; } = SubjectKind.Guild;
}

/// <summary>The instance's credit settings.</summary>
public sealed class CreditOptions
{
    public const string SectionName = "Credit";

    /// <summary>The internal peg used to set point prices, in points per euro.</summary>
    public const long PointsPerEuroPeg = 100;

    /// <summary>The most credit one account may hold, in points.</summary>
    public long MaxWalletBalancePoints { get; set; } = 200 * PointsPerEuroPeg;

    /// <summary>How long a lot lives when the campaign does not say.</summary>
    public int DefaultLotLifetimeDays { get; set; } = 365;

    /// <summary>How far ahead of a lot's expiry the warning goes out.</summary>
    public int ExpiryWarningDays { get; set; } = 30;

    /// <summary>The spend catalogue.</summary>
    public List<CreditSkuOptions> Catalogue { get; set; } = [];
}

/// <summary>One SKU, validated.</summary>
public sealed record CreditSku(
    string Code,
    string Title,
    string? Description,
    long PricePoints,
    string Plan,
    int DurationDays,
    SubjectKind Subject,
    long? CashPriceMinorUnits,
    string? CashCurrency)
{
    public CreditSkuKind Kind => CreditSkuKind.TimeBoxedPlanGrant;

    /// <summary>Whether this SKU may be offered or bought at all.</summary>
    public bool HasCashPrice => CashPriceMinorUnits is > 0 && !string.IsNullOrWhiteSpace(CashCurrency);
}
