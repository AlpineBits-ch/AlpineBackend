using System.Text.Json.Serialization;
using Billing.Application.Dtos;
using Billing.Domain.Aggregates;
using Billing.Domain.Promotions;
using Echo.Entitlements.Model;

namespace Billing.Application.Promotions;

/// <summary>A refusal, in the shape the credit surface already uses.</summary>
public sealed record PromotionErrorDto(string Code, string Message, IReadOnlyList<string> FailedRules);

/// <summary>What the console posts to open a campaign.</summary>
public sealed record CreatePromotionCampaignRequest(
    string Code,
    string Description,
    string Plan,
    int TrialDays,
    long TotalBudgetRedemptions,
    SubjectKind SubjectKind = SubjectKind.Guild,
    List<string>? RequiredRules = null,
    int MinimumAccountAgeDays = 0,
    int MaxPerSubject = 1,
    int AlertThresholdPercent = 80,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record SetPromotionBudgetRequest(long TotalBudgetRedemptions);

/// <summary>One campaign as the console reads it.</summary>
public sealed record PromotionCampaignDto(
    string Id,
    string Code,
    string Description,
    string Plan,
    int TrialDays,
    [property: JsonConverter(typeof(LowercaseSubjectKindConverter))] SubjectKind SubjectKind,
    long TotalBudgetRedemptions,
    long IssuedRedemptions,
    long RemainingRedemptions,
    int MaxPerSubject,
    IReadOnlyList<string> RequiredRules,
    int MinimumAccountAgeDays,
    int AlertThresholdPercent,
    DateTimeOffset? AlertedAt,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? PausedAt,
    string? PausedBy,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static PromotionCampaignDto From(PromotionCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return new PromotionCampaignDto(
            campaign.Id,
            campaign.Code,
            campaign.Description,
            campaign.Plan,
            campaign.TrialDays,
            campaign.SubjectKind,
            campaign.TotalBudgetRedemptions,
            campaign.IssuedRedemptions,
            campaign.RemainingRedemptions,
            campaign.MaxPerSubject,
            PromotionEligibilityMap.Names(campaign.RequiredSignals),
            campaign.MinimumAccountAgeDays,
            campaign.AlertThresholdPercent,
            campaign.AlertedAt,
            campaign.StartsAt,
            campaign.EndsAt,
            campaign.PausedAt,
            campaign.PausedBy,
            campaign.CreatedBy,
            campaign.CreatedAt);
    }
}

/// <summary>One redemption, for the console.</summary>
public sealed record PromotionRedemptionDto(
    string Id,
    string CampaignId,
    [property: JsonConverter(typeof(LowercaseSubjectKindConverter))] SubjectKind SubjectKind,
    string SubjectId,
    string OwnerUserId,
    DateTimeOffset RedeemedAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? ExpiredAt,
    DateTimeOffset? ReleasedAt)
{
    public static PromotionRedemptionDto From(PromotionRedemption redemption)
    {
        ArgumentNullException.ThrowIfNull(redemption);

        return new PromotionRedemptionDto(
            redemption.Id,
            redemption.CampaignId,
            redemption.SubjectKind,
            redemption.SubjectId,
            redemption.OwnerUserId,
            redemption.RedeemedAt,
            redemption.EndsAt,
            redemption.ExpiredAt,
            redemption.ReleasedAt);
    }
}

/// <summary>One eligibility rule, so a campaign editor can offer the list rather than hard-coding
/// it. The catalogue is the source of truth and this is it on the wire.</summary>
public sealed record PromotionRuleDto(string Code, string Description);
