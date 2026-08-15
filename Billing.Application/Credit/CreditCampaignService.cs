using Billing.Domain.Aggregates;
using Billing.Domain.Campaigns;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Credit;

/// <summary>Refusals that belong to campaign administration.</summary>
public static class CreditCampaignErrorCodes
{
    public const string CodeRequired = "campaign_code_required";
    public const string DescriptionRequired = "campaign_description_required";
    public const string BudgetRequired = "campaign_budget_required";
    public const string ThresholdOutOfRange = "campaign_threshold_out_of_range";
    public const string DuplicateCode = "campaign_code_taken";
    public const string NotFound = "campaign_not_found";
    public const string BudgetBelowIssued = "campaign_budget_below_issued";
}

/// <summary>What the console posts to open a campaign.</summary>
public sealed record CreateCreditCampaign(
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

/// <summary>
/// Opening, capping and stopping the campaigns credit is issued under (monetization.md section
/// 8.6).
/// </summary>
public sealed class CreditCampaignService(MicroserviceContext db, TimeProvider clock)
{
    public async Task<CreditCampaign> CreateAsync(
        CreateCreditCampaign request, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.CodeRequired,
                "A campaign needs a code. It is what an issuance names and what the budget is counted "
                + "against.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.DescriptionRequired,
                "A campaign needs a description. Somebody will read it a year from now while working "
                + "out where a lot came from.");
        }

        if (request.TotalBudgetPoints <= 0)
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.BudgetRequired,
                "A campaign needs a positive total budget. There is deliberately no unlimited value: "
                + "an uncapped campaign is one loop away from being a five-figure liability, which is "
                + "the failure monetization.md section 8.6 exists to prevent.");
        }

        if (!CampaignBudget.IsThresholdInRange(request.AlertThresholdPercent))
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.ThresholdOutOfRange,
                $"The alert threshold is {request.AlertThresholdPercent}%, which is not a percentage "
                + "between 1 and 100. The alert has to fire before the cap, not at it - a campaign "
                + "that has already stopped is one somebody finds out about from a support ticket.");
        }

        var code = request.Code.Trim();

        if (await db.CreditCampaigns.AnyAsync(campaign => campaign.Code == code, cancellationToken))
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.DuplicateCode,
                $"There is already a campaign called '{code}'. Two would make 'how much has this "
                + "campaign spent' ambiguous in the one place there is no way to ask.");
        }

        var now = clock.GetUtcNow();

        var campaign = new CreditCampaign
        {
            Id = CreditCampaign.GenerateId(),
            Code = code,
            Description = request.Description.Trim(),
            TotalBudgetPoints = request.TotalBudgetPoints,
            IssuedPoints = 0,
            MaxPerUserPoints = request.MaxPerUserPoints,
            DefaultIssuePoints = request.DefaultIssuePoints,
            LotLifetimeDays = request.LotLifetimeDays,
            AlertThresholdPercent = request.AlertThresholdPercent,
            Automated = request.Automated,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.CreditCampaigns.Add(campaign);

        return campaign;
    }

    public Task<CreditCampaign?> FindAsync(string codeOrId, CancellationToken cancellationToken) =>
        db.CreditCampaigns.FirstOrDefaultAsync(
            campaign => campaign.Id == codeOrId || campaign.Code == codeOrId, cancellationToken);

    /// <summary>Every campaign, most recently opened first.</summary>
    public Task<List<CreditCampaign>> ListAsync(CancellationToken cancellationToken) =>
        db.CreditCampaigns.OrderByDescending(campaign => campaign.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Stops a campaign issuing without ending the record of it.</summary>
    public async Task<CreditCampaign> PauseAsync(
        string codeOrId, string pausedBy, CancellationToken cancellationToken)
    {
        var campaign = await FindAsync(codeOrId, cancellationToken)
                       ?? throw new CreditRefusedException(
                           CreditCampaignErrorCodes.NotFound, $"No credit campaign '{codeOrId}'.");

        if (campaign.IsPaused) return campaign;

        campaign.PausedAt = clock.GetUtcNow();
        campaign.PausedBy = pausedBy;

        return campaign;
    }

    public async Task<CreditCampaign> ResumeAsync(string codeOrId, CancellationToken cancellationToken)
    {
        var campaign = await FindAsync(codeOrId, cancellationToken)
                       ?? throw new CreditRefusedException(
                           CreditCampaignErrorCodes.NotFound, $"No credit campaign '{codeOrId}'.");

        campaign.PausedAt = null;
        campaign.PausedBy = null;

        return campaign;
    }

    /// <summary>Moves the budget.</summary>
    public async Task<CreditCampaign> SetBudgetAsync(
        string codeOrId, long totalBudgetPoints, CancellationToken cancellationToken)
    {
        var campaign = await FindAsync(codeOrId, cancellationToken)
                       ?? throw new CreditRefusedException(
                           CreditCampaignErrorCodes.NotFound, $"No credit campaign '{codeOrId}'.");

        if (totalBudgetPoints <= 0)
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.BudgetRequired,
                "A campaign budget has to be positive. Pause the campaign to stop it issuing.");
        }

        if (totalBudgetPoints < campaign.IssuedPoints)
        {
            throw new CreditRefusedException(CreditCampaignErrorCodes.BudgetBelowIssued,
                $"Campaign '{campaign.Code}' has already issued {campaign.IssuedPoints} points, so a "
                + $"budget of {totalBudgetPoints} would claim less was spent than was. Pause it "
                + "instead.");
        }

        campaign.TotalBudgetPoints = totalBudgetPoints;

        // A raised budget puts the campaign back below its alert threshold, so the next crossing
        // should be announced again.
        if (campaign.IssuedPoints < campaign.AlertAtPoints) campaign.AlertedAt = null;

        return campaign;
    }
}
