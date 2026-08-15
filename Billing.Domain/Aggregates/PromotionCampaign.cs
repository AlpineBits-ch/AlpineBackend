using Billing.Domain.Campaigns;
using Billing.Domain.Promotions;
using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>
/// A budgeted reason to confer a plan for a while: a trial, a partner offer, a win-back, a
/// beta-tester grant.
/// </summary>
public class PromotionCampaign : BaseEntity<PromotionCampaign>, IPrefixedEntity
{
    public static string Prefix { get; } = "pcmp";

    /// <summary>The stable key a redemption names.</summary>
    public string Code { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>The plan the trial confers, matching <c>Plan.Name</c>.</summary>
    public string Plan { get; set; } = null!;

    /// <summary>How long the trial runs.</summary>
    public int TrialDays { get; set; }

    /// <summary>What the plan is conferred on.</summary>
    public SubjectKind SubjectKind { get; set; } = SubjectKind.Guild;

    /// <summary>The hard cap on how many redemptions this campaign may ever produce.</summary>
    public long TotalBudgetRedemptions { get; set; }

    /// <summary>What has actually been redeemed against it.</summary>
    public long IssuedRedemptions { get; set; }

    /// <summary>How many times one subject may redeem this campaign.</summary>
    public int MaxPerSubject { get; set; } = 1;

    /// <summary>What this campaign requires of the account redeeming it.</summary>
    public PromotionEligibility RequiredSignals { get; set; } = PromotionEligibility.None;

    /// <summary>Read only by <see cref="PromotionEligibility.MinimumAccountAge"/>, and meaningless
    /// without it.</summary>
    public int MinimumAccountAgeDays { get; set; }

    public int AlertThresholdPercent { get; set; } = CampaignBudget.DefaultAlertThresholdPercent;

    public DateTimeOffset? AlertedAt { get; set; }

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public string? PausedBy { get; set; }

    public string CreatedBy { get; set; } = null!;

    public long RemainingRedemptions =>
        CampaignBudget.Remaining(TotalBudgetRedemptions, IssuedRedemptions);

    public bool IsPaused => PausedAt is not null;

    /// <summary>Whether a card is required, which is the one eligibility rule the checkout flow has to
    /// know about before it can decide what to ask the client for.</summary>
    public bool RequiresCard => RequiredSignals.HasFlag(PromotionEligibility.PaymentCard);

    /// <inheritdoc cref="CampaignBudget.IsOpenAt"/>
    public bool IsOpenAt(DateTimeOffset instant) =>
        CampaignBudget.IsOpenAt(instant, StartsAt, EndsAt, PausedAt);

    /// <inheritdoc cref="CampaignBudget.AlertAt"/>
    public long AlertAtRedemptions =>
        CampaignBudget.AlertAt(TotalBudgetRedemptions, AlertThresholdPercent);

    /// <summary>Whether one more redemption fits under the cap.</summary>
    public bool HasRoomFor(long redemptions) =>
        CampaignBudget.Fits(TotalBudgetRedemptions, IssuedRedemptions, redemptions);

    /// <inheritdoc cref="CampaignBudget.ShouldAlert"/>
    public bool ShouldAlert() => CampaignBudget.ShouldAlert(
        TotalBudgetRedemptions, IssuedRedemptions, AlertThresholdPercent, AlertedAt);
}
