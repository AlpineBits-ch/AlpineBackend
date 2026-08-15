namespace Billing.Domain.Campaigns;

/// <summary>
/// The budget, alert and pause arithmetic every campaign shares, as functions rather than as a base
/// class or a shared table.
/// </summary>
public static class CampaignBudget
{
    /// <summary>Where the alert sits when nobody chooses.</summary>
    public const int DefaultAlertThresholdPercent = 80;

    /// <summary>Whether a threshold is a percentage at all.</summary>
    public static bool IsThresholdInRange(int percent) => percent is >= 1 and <= 100;

    /// <summary>Never negative, so a budget lowered to exactly what has gone out reads as zero left
    /// rather than as a debt.</summary>
    public static long Remaining(long total, long issued) => Math.Max(0, total - issued);

    /// <summary>The point at which the alert is due.</summary>
    public static long AlertAt(long total, int thresholdPercent) =>
        (long)(total * (thresholdPercent / 100.0));

    /// <summary>Whether the campaign may act at all right now.</summary>
    public static bool IsOpenAt(
        DateTimeOffset instant,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        DateTimeOffset? pausedAt) =>
        pausedAt is null
        && (startsAt is null || startsAt <= instant)
        && (endsAt is null || endsAt > instant);

    /// <summary>Whether this much more fits under the cap.</summary>
    public static bool Fits(long total, long issued, long amount) => issued + amount <= total;

    /// <summary><b>Once, not per issuance.</b> A campaign past its threshold is past it on every
    /// subsequent write, so the stamp is what stops the warning repeating for the rest of the
    /// campaign's life and lets the console show which ones are close.</summary>
    public static bool ShouldAlert(long total, long issued, int thresholdPercent, DateTimeOffset? alertedAt) =>
        alertedAt is null && issued >= AlertAt(total, thresholdPercent);
}
