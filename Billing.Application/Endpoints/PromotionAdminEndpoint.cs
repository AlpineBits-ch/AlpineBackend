using System.Security.Claims;
using Billing.Application.Promotions;
using Billing.Application.Security;
using Billing.Domain.Promotions;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>The staff surface for promotion campaigns.</summary>
public class PromotionAdminEndpoint
{
    /// <summary>Every campaign, most recently opened first.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/promotions/campaigns")]
    public static async Task<IResult> CampaignsAsync(
        [NotBody] PromotionCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        var rows = await campaigns.ListAsync(cancellationToken);
        return Results.Ok(rows.Select(PromotionCampaignDto.From).ToList());
    }

    /// <summary>The eligibility rules a campaign can require, from the one table that says what each
    /// one means. Served rather than hard-coded into the console, so a rule added here appears in the
    /// campaign editor without a second deploy.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/promotions/rules")]
    public static IResult Rules() =>
        Results.Ok(PromotionEligibilityMap.All
            .Select(rule => new PromotionRuleDto(rule.Code, rule.Refusal))
            .ToList());

    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/promotions/campaigns/{codeOrId}")]
    public static async Task<IResult> CampaignAsync(
        string codeOrId,
        [NotBody] PromotionCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        var campaign = await campaigns.FindAsync(codeOrId, cancellationToken);

        return campaign is null
            ? Results.NotFound()
            : Results.Ok(PromotionCampaignDto.From(campaign));
    }

    /// <summary>Who has redeemed a campaign.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/promotions/campaigns/{codeOrId}/redemptions")]
    public static async Task<IResult> RedemptionsAsync(
        string codeOrId,
        int limit,
        [NotBody] PromotionCampaignService campaigns,
        [NotBody] PromotionRedemptionService redemptions,
        CancellationToken cancellationToken)
    {
        var campaign = await campaigns.FindAsync(codeOrId, cancellationToken);
        if (campaign is null) return Results.NotFound();

        var rows = await redemptions.ListForCampaignAsync(
            campaign.Id, limit <= 0 ? 200 : limit, cancellationToken);

        return Results.Ok(rows.Select(PromotionRedemptionDto.From).ToList());
    }

    /// <summary>Everything one subject has ever redeemed, expired and released rows included. The
    /// answer to "why can this account not start a trial", which is otherwise unanswerable.</summary>
    [Authorize(Policy = BillingPolicies.GrantRead)]
    [WolverineGet("/api/v1/promotions/subjects/{subjectKind}/{subjectId}/redemptions")]
    public static async Task<IResult> SubjectRedemptionsAsync(
        string subjectKind,
        string subjectId,
        [NotBody] PromotionRedemptionService redemptions,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SubjectKind>(subjectKind, ignoreCase: true, out var kind))
        {
            return Results.BadRequest(new PromotionErrorDto(
                PromotionErrorCodes.UnknownCampaign,
                $"'{subjectKind}' is not a subject kind. Expected '{EntitlementSubjectKinds.Guild}' or "
                + $"'{EntitlementSubjectKinds.User}'.",
                []));
        }

        var rows = await redemptions.ListForSubjectAsync(
            new EntitlementSubject(kind, subjectId), cancellationToken);

        return Results.Ok(rows.Select(PromotionRedemptionDto.From).ToList());
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/promotions/campaigns")]
    public static async Task<IResult> CreateCampaignAsync(
        CreatePromotionCampaignRequest request,
        [NotBody] PromotionCampaignService campaigns,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            var campaign = await campaigns.CreateAsync(
                new CreatePromotionCampaign(
                    request.Code,
                    request.Description,
                    request.Plan,
                    request.TrialDays,
                    request.TotalBudgetRedemptions,
                    request.SubjectKind,
                    request.RequiredRules,
                    request.MinimumAccountAgeDays,
                    request.MaxPerSubject,
                    request.AlertThresholdPercent,
                    request.StartsAt,
                    request.EndsAt),
                actor,
                cancellationToken);

            return Results.Ok(PromotionCampaignDto.From(campaign));
        }
        catch (PromotionRefusedException refusal)
        {
            return Refused(refusal);
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/promotions/campaigns/{codeOrId}/pause")]
    public static async Task<IResult> PauseCampaignAsync(
        string codeOrId,
        [NotBody] PromotionCampaignService campaigns,
        [NotBody] ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        var actor = ActorId(caller);
        if (actor is null) return Results.Unauthorized();

        try
        {
            return Results.Ok(PromotionCampaignDto.From(
                await campaigns.PauseAsync(codeOrId, actor, cancellationToken)));
        }
        catch (PromotionRefusedException refusal)
        {
            return Refused(refusal);
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePost("/api/v1/promotions/campaigns/{codeOrId}/resume")]
    public static async Task<IResult> ResumeCampaignAsync(
        string codeOrId,
        [NotBody] PromotionCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(PromotionCampaignDto.From(
                await campaigns.ResumeAsync(codeOrId, cancellationToken)));
        }
        catch (PromotionRefusedException refusal)
        {
            return Refused(refusal);
        }
    }

    [Authorize(Policy = BillingPolicies.GrantAdmin)]
    [WolverinePatch("/api/v1/promotions/campaigns/{codeOrId}/budget")]
    public static async Task<IResult> SetCampaignBudgetAsync(
        string codeOrId,
        SetPromotionBudgetRequest request,
        [NotBody] PromotionCampaignService campaigns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Results.Ok(PromotionCampaignDto.From(
                await campaigns.SetBudgetAsync(
                    codeOrId, request.TotalBudgetRedemptions, cancellationToken)));
        }
        catch (PromotionRefusedException refusal)
        {
            return Refused(refusal);
        }
    }

    /// <summary>The refusal codes keep their meaning across the wire rather than being flattened to a
    /// 400: "no such campaign" is a 404 everywhere it can happen, and the console switches on the code
    /// for the rest.</summary>
    private static IResult Refused(PromotionRefusedException refusal) =>
        refusal.Code == PromotionErrorCodes.UnknownCampaign
            ? Results.NotFound(new PromotionErrorDto(refusal.Code, refusal.Message, refusal.FailedRules))
            : Results.BadRequest(new PromotionErrorDto(refusal.Code, refusal.Message, refusal.FailedRules));

    private static string? ActorId(ClaimsPrincipal caller)
    {
        var actor = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(actor) ? null : actor;
    }
}
