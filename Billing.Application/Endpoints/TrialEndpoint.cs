using System.Security.Claims;
using Billing.Application.Promotions;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>Starting a trial and moving one, for the person taking it.</summary>
public class TrialEndpoint
{
    /// <summary>Starts a trial.</summary>
    [Authorize]
    [WolverinePost("/api/v1/trials")]
    public static async Task<IResult> StartAsync(
        StartTrialRequest request,
        [NotBody] TrialService trials,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<TrialEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () =>
            {
                try
                {
                    var started = await trials.StartAsync(
                        request.CampaignCode, request.GuildId, request.PaymentMethodId, userId,
                        cancellationToken);

                    return Results.Ok(new StartTrialResponse(
                        started.Campaign.Code,
                        started.Campaign.Plan,
                        started.Owner.EndsAt,
                        started.Subscription,
                        started.ClientSecret));
                }
                catch (PromotionRefusedException refusal)
                {
                    return Refused(refusal);
                }
            },
            logger);
    }

    /// <summary>Moves a live trial onto another server, keeping the clock.</summary>
    [Authorize]
    [WolverinePost("/api/v1/trials/{campaignCode}/move")]
    public static async Task<(IResult, OutgoingMessages)> MoveAsync(
        string campaignCode,
        MoveTrialRequest request,
        [NotBody] TrialService trials,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<TrialEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new OutgoingMessages();

        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return (BillingProblems.Unauthenticated(), messages);

        var result = await BillingProblems.GuardAsync(
            async () =>
            {
                try
                {
                    var moved = await trials.MoveAsync(
                        campaignCode, request.GuildId, userId, cancellationToken);

                    foreach (var announcement in moved.Announcements) messages.Add(announcement);

                    return Results.Ok(new MoveTrialResponse(
                        campaignCode, moved.Guild.SubjectId, moved.Guild.EndsAt));
                }
                catch (PromotionRefusedException refusal)
                {
                    return Refused(refusal);
                }
            },
            logger);

        return (result, messages);
    }

    /// <summary>Whether this account could take this campaign right now.</summary>
    [Authorize]
    [WolverineGet("/api/v1/trials/{campaignCode}/eligibility")]
    public static async Task<IResult> EligibilityAsync(
        string campaignCode,
        string? guildId,
        [NotBody] TrialService trials,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<TrialEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(new TrialEligibilityDto(
                campaignCode,
                await trials.IsEligibleAsync(campaignCode, guildId, userId, cancellationToken))),
            logger);
    }

    /// <summary>A promotion refusal as problem+json, with the status the code means.</summary>
    private static IResult Refused(PromotionRefusedException refusal)
    {
        var status = refusal.Code switch
        {
            PromotionErrorCodes.UnknownCampaign => StatusCodes.Status404NotFound,

            PromotionErrorCodes.AlreadyRedeemed
                or PromotionErrorCodes.GuildAlreadyRedeemed
                or PromotionErrorCodes.IdentityAlreadyRedeemed
                or PromotionErrorCodes.CampaignClosed
                or PromotionErrorCodes.CampaignBudgetExhausted
                or PromotionErrorCodes.NoTrialToMove => StatusCodes.Status409Conflict,

            PromotionErrorCodes.NotEligible
                or PromotionErrorCodes.NotPermitted
                or PromotionErrorCodes.TargetRequired => StatusCodes.Status403Forbidden,

            PromotionErrorCodes.SignalsUnavailable => StatusCodes.Status503ServiceUnavailable,

            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Problem(
            detail: refusal.Message,
            statusCode: status,
            title: "Billing",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = refusal.Code,

                // Only for not_eligible, where it is the list of rules the account did not satisfy
                // and is what lets a client say which.
                ["failedRules"] = refusal.FailedRules,
            });
    }
}
