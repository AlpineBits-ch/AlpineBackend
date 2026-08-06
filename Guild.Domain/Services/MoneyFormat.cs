using System.Globalization;

namespace Guild.Domain.Services;

/// <summary>
/// Renders a minor-unit amount as something a person reads in a push notification.
/// </summary>
public static class MoneyFormat
{
    private static readonly HashSet<string> ZeroExponentCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW",
        "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    /// <summary>"CHF 42.50", "JPY 4250".</summary>
    public static string Format(long amountMinor, string currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? "CHF" : currency.Trim().ToUpperInvariant();

        if (ZeroExponentCurrencies.Contains(code))
            return $"{code} {amountMinor.ToString(CultureInfo.InvariantCulture)}";

        // Formatted off the absolute value with the sign re-applied, so -5 renders as "-0.05"
        // rather than "-0.-5".
        var sign = amountMinor < 0 ? "-" : "";
        var magnitude = Math.Abs(amountMinor);

        return $"{code} {sign}{magnitude / 100}.{magnitude % 100:00}";
    }
}
