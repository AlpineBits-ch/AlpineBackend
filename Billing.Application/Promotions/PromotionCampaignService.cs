using Billing.Domain.Aggregates;
using Billing.Domain.Campaigns;
using Billing.Domain.Promotions;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Promotions;

/// <summary>Machine-readable refusals from the promotion surface.</summary>
public static class PromotionErrorCodes
{
    public const string CodeRequired = "campaign_code_required";
    public const string DescriptionRequired = "campaign_description_required";
    public const string BudgetRequired = "campaign_budget_required";
    public const string ThresholdOutOfRange = "campaign_threshold_out_of_range";
    public const string DuplicateCode = "campaign_code_taken";
    public const string UnknownCampaign = "unknown_campaign";
    public const string BudgetBelowIssued = "campaign_budget_below_issued";
    public const string PlanRequired = "campaign_plan_required";
    public const string TrialDaysRequired = "campaign_trial_days_required";
    public const string UnknownRule = "campaign_unknown_rule";

    /// <summary>The campaign is paused, or outside its validity window.</summary>
    public const string CampaignClosed = "campaign_closed";

    public const string CampaignBudgetExhausted = "campaign_budget_exhausted";

    /// <summary>This account has already had this campaign.</summary>
    public const string AlreadyRedeemed = "already_redeemed";

    /// <summary>This guild has already had this campaign, whoever owned it at the time.</summary>
    public const string GuildAlreadyRedeemed = "guild_already_redeemed";

    /// <summary>A device, phone number or card that has already redeemed this campaign under another
    /// account.</summary>
    public const string IdentityAlreadyRedeemed = "identity_already_redeemed";

    /// <summary>The account does not meet what the campaign asks of it.</summary>
    public const string NotEligible = "not_eligible";

    /// <summary>A guild is needed and none was named, or the caller does not manage the one that
    /// was.</summary>
    public const string TargetRequired = "target_required";

    public const string NotPermitted = "not_permitted";

    /// <summary>Identity or Guild did not answer.</summary>
    public const string SignalsUnavailable = "signals_unavailable";

    /// <summary>Asked to move a trial that does not exist, or that has already ended.</summary>
    public const string NoTrialToMove = "no_trial_to_move";
}

/// <summary>A promotion operation was refused.</summary>
public sealed class PromotionRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The eligibility rules that failed, for <see cref="PromotionErrorCodes.NotEligible"/>.
    /// Empty for every other code.</summary>
    public IReadOnlyList<string> FailedRules { get; init; } = [];
}

/// <summary>What the console posts to open a campaign.</summary>
public sealed record CreatePromotionCampaign(
    string Code,
    string Description,
    string Plan,
    int TrialDays,
    long TotalBudgetRedemptions,
    SubjectKind SubjectKind = SubjectKind.Guild,
    IReadOnlyList<string>? RequiredRules = null,
    int MinimumAccountAgeDays = 0,
    int MaxPerSubject = 1,
    int AlertThresholdPercent = CampaignBudget.DefaultAlertThresholdPercent,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

/// <summary>Opening, capping and stopping the campaigns trials are redeemed under.</summary>
public sealed class PromotionCampaignService(MicroserviceContext db, TimeProvider clock)
{
    public async Task<PromotionCampaign> CreateAsync(
        CreatePromotionCampaign request, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.CodeRequired,
                "A campaign needs a code. It is what a redemption names and what the budget is counted "
                + "against.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.DescriptionRequired,
                "A campaign needs a description. Somebody will read it a year from now while working "
                + "out why an account was refused a trial.");
        }

        if (string.IsNullOrWhiteSpace(request.Plan))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.PlanRequired,
                "A campaign needs the plan it confers. A campaign that grants nothing is a row nobody "
                + "can explain later.");
        }

        if (request.TrialDays <= 0)
        {
            throw new PromotionRefusedException(PromotionErrorCodes.TrialDaysRequired,
                $"A trial of {request.TrialDays} days is not a trial. Section 7.5 is explicit that "
                + "anything issued during a promotion carries a date set at the time of the promise - "
                + "an open-ended one is a pricing decision made by accident.");
        }

        if (request.TotalBudgetRedemptions <= 0)
        {
            throw new PromotionRefusedException(PromotionErrorCodes.BudgetRequired,
                "A campaign needs a positive total budget. There is deliberately no unlimited value: "
                + "an uncapped campaign is one loop away from handing out every plan on the instance, "
                + "which is the failure monetization.md section 8.6 exists to prevent.");
        }

        if (!CampaignBudget.IsThresholdInRange(request.AlertThresholdPercent))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.ThresholdOutOfRange,
                $"The alert threshold is {request.AlertThresholdPercent}%, which is not a percentage "
                + "between 1 and 100. The alert has to fire before the cap, not at it - a campaign "
                + "that has already stopped is one somebody finds out about from a support ticket.");
        }

        PromotionEligibility required;

        try
        {
            required = PromotionEligibilityMap.Parse(request.RequiredRules);
        }
        catch (ArgumentException exception)
        {
            // Refused rather than ignored.
            throw new PromotionRefusedException(PromotionErrorCodes.UnknownRule, exception.Message);
        }

        var code = request.Code.Trim();

        if (await db.PromotionCampaigns.AnyAsync(campaign => campaign.Code == code, cancellationToken))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.DuplicateCode,
                $"There is already a campaign called '{code}'. Two would make 'how many redemptions "
                + "has this campaign had' ambiguous in the one place there is no way to ask.");
        }

        var now = clock.GetUtcNow();

        var created = new PromotionCampaign
        {
            Id = PromotionCampaign.GenerateId(),
            Code = code,
            Description = request.Description.Trim(),
            Plan = request.Plan.Trim().ToLowerInvariant(),
            TrialDays = request.TrialDays,
            SubjectKind = request.SubjectKind,
            TotalBudgetRedemptions = request.TotalBudgetRedemptions,
            IssuedRedemptions = 0,
            MaxPerSubject = Math.Max(1, request.MaxPerSubject),
            RequiredSignals = required,
            MinimumAccountAgeDays = Math.Max(0, request.MinimumAccountAgeDays),
            AlertThresholdPercent = request.AlertThresholdPercent,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PromotionCampaigns.Add(created);

        return created;
    }

    public Task<PromotionCampaign?> FindAsync(string? codeOrId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codeOrId)) return Task.FromResult<PromotionCampaign?>(null);

        var key = codeOrId.Trim();

        return db.PromotionCampaigns.FirstOrDefaultAsync(
            campaign => campaign.Id == key || campaign.Code == key, cancellationToken);
    }

    public async Task<PromotionCampaign> RequireAsync(
        string? codeOrId, CancellationToken cancellationToken) =>
        await FindAsync(codeOrId, cancellationToken)
        ?? throw new PromotionRefusedException(
            PromotionErrorCodes.UnknownCampaign, $"'{codeOrId}' is not a campaign on this instance.");

    /// <summary>Every campaign, most recently opened first.</summary>
    public Task<List<PromotionCampaign>> ListAsync(CancellationToken cancellationToken) =>
        db.PromotionCampaigns
            .OrderByDescending(campaign => campaign.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Stops a campaign redeeming without ending the record of it.</summary>
    public async Task<PromotionCampaign> PauseAsync(
        string codeOrId, string pausedBy, CancellationToken cancellationToken)
    {
        var campaign = await RequireAsync(codeOrId, cancellationToken);

        if (campaign.IsPaused) return campaign;

        campaign.PausedAt = clock.GetUtcNow();
        campaign.PausedBy = pausedBy;

        return campaign;
    }

    public async Task<PromotionCampaign> ResumeAsync(string codeOrId, CancellationToken cancellationToken)
    {
        var campaign = await RequireAsync(codeOrId, cancellationToken);

        campaign.PausedAt = null;
        campaign.PausedBy = null;

        return campaign;
    }

    /// <summary>Moves the budget.</summary>
    public async Task<PromotionCampaign> SetBudgetAsync(
        string codeOrId, long totalBudgetRedemptions, CancellationToken cancellationToken)
    {
        var campaign = await RequireAsync(codeOrId, cancellationToken);

        if (totalBudgetRedemptions <= 0)
        {
            throw new PromotionRefusedException(PromotionErrorCodes.BudgetRequired,
                "A campaign budget has to be positive. Pause the campaign to stop it redeeming.");
        }

        if (totalBudgetRedemptions < campaign.IssuedRedemptions)
        {
            throw new PromotionRefusedException(PromotionErrorCodes.BudgetBelowIssued,
                $"Campaign '{campaign.Code}' has already been redeemed {campaign.IssuedRedemptions} "
                + $"times, so a budget of {totalBudgetRedemptions} would claim fewer people got it than "
                + "did. Pause it instead.");
        }

        campaign.TotalBudgetRedemptions = totalBudgetRedemptions;

        // A raised budget puts the campaign back below its alert threshold, so the next crossing
        // should be announced again.
        if (campaign.IssuedRedemptions < campaign.AlertAtRedemptions) campaign.AlertedAt = null;

        return campaign;
    }

    /// <summary>
    /// Charges one redemption against the campaign, and stamps the alert if this is the crossing.
    /// </summary>
    public void Charge(
        PromotionCampaign campaign, DateTimeOffset now, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        if (!campaign.IsOpenAt(now))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.CampaignClosed,
                campaign.IsPaused
                    ? $"Campaign '{campaign.Code}' is paused."
                    : $"Campaign '{campaign.Code}' is outside its validity window.");
        }

        if (!campaign.HasRoomFor(1))
        {
            logger?.LogError(
                "Campaign {Code} refused a redemption: all {Budget} of its budget is spent. Raise it "
                + "deliberately or leave it stopped.",
                campaign.Code, campaign.TotalBudgetRedemptions);

            throw new PromotionRefusedException(PromotionErrorCodes.CampaignBudgetExhausted,
                $"Campaign '{campaign.Code}' has run out: all {campaign.TotalBudgetRedemptions} of its "
                + "redemptions have been taken.");
        }

        campaign.IssuedRedemptions += 1;

        if (campaign.ShouldAlert())
        {
            campaign.AlertedAt = now;

            logger?.LogWarning(
                "Campaign {Code} has been redeemed {Issued} of {Budget} times, past its {Threshold}% "
                + "alert. {Remaining} left.",
                campaign.Code, campaign.IssuedRedemptions, campaign.TotalBudgetRedemptions,
                campaign.AlertThresholdPercent, campaign.RemainingRedemptions);
        }
    }
}
