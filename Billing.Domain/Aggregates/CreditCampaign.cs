using Billing.Domain.Campaigns;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>A budgeted reason to hand out credit.</summary>
public class CreditCampaign : BaseEntity<CreditCampaign>, IPrefixedEntity
{
    public static string Prefix { get; } = "ccmp";

    /// <summary>The stable key an issuance names.</summary>
    public string Code { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>The hard cap, in points, over the campaign's whole life.</summary>
    public long TotalBudgetPoints { get; set; }

    /// <summary>What has actually been issued against it.</summary>
    public long IssuedPoints { get; set; }

    /// <summary>The per-recipient cap, or null for none.</summary>
    public long? MaxPerUserPoints { get; set; }

    /// <summary>The default size of one issuance from this campaign, when the caller does not name
    /// an amount. Null means the caller must always say.</summary>
    public long? DefaultIssuePoints { get; set; }

    /// <summary>How long a lot issued by this campaign lives.</summary>
    public int? LotLifetimeDays { get; set; }

    /// <summary>Percentage of <see cref="TotalBudgetPoints"/> at which the alert fires.</summary>
    public int AlertThresholdPercent { get; set; } = CampaignBudget.DefaultAlertThresholdPercent;

    public DateTimeOffset? AlertedAt { get; set; }

    /// <summary>True when this campaign issues with no human in the loop.</summary>
    public bool Automated { get; set; }

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public string? PausedBy { get; set; }

    public string CreatedBy { get; set; } = null!;

    public long RemainingPoints => CampaignBudget.Remaining(TotalBudgetPoints, IssuedPoints);

    public bool IsPaused => PausedAt is not null;

    /// <inheritdoc cref="CampaignBudget.IsOpenAt"/>
    public bool IsOpenAt(DateTimeOffset instant) =>
        CampaignBudget.IsOpenAt(instant, StartsAt, EndsAt, PausedAt);

    /// <inheritdoc cref="CampaignBudget.AlertAt"/>
    public long AlertAtPoints => CampaignBudget.AlertAt(TotalBudgetPoints, AlertThresholdPercent);

    /// <inheritdoc cref="CampaignBudget.ShouldAlert"/>
    public bool ShouldAlert() =>
        CampaignBudget.ShouldAlert(TotalBudgetPoints, IssuedPoints, AlertThresholdPercent, AlertedAt);
}
