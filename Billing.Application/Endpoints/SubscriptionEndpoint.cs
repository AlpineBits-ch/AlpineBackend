using System.Security.Claims;
using Billing.Application.Dtos;
using Billing.Application.Notifications;
using Billing.Application.Services;
using Identity.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>Starting, reading, cancelling, resuming and changing a subscription.</summary>
public class SubscriptionEndpoint
{
    [Authorize]
    [WolverinePost("/api/v1/subscriptions")]
    public static async Task<IResult> CreateAsync(
        CreateSubscriptionRequest request,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await subscriptions.CreateAsync(request, userId, cancellationToken)),
            logger);
    }

    [Authorize]
    [WolverineGet("/api/v1/subscriptions")]
    public static async Task<IResult> ListAsync(
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await subscriptions.ListAsync(userId, cancellationToken)),
            logger);
    }

    [Authorize]
    [WolverineGet("/api/v1/subscriptions/{id}")]
    public static async Task<IResult> GetAsync(
        string id,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await subscriptions.GetAsync(id, userId, cancellationToken)),
            logger);
    }

    /// <summary>Ends the subscription at the end of the period that was paid for.</summary>
    [Authorize]
    [WolverinePost("/api/v1/subscriptions/{id}/cancel")]
    public static async Task<IResult> CancelAsync(
        string id,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await subscriptions.CancelAsync(id, userId, cancellationToken)),
            logger);
    }

    [Authorize]
    [WolverinePost("/api/v1/subscriptions/{id}/resume")]
    public static async Task<IResult> ResumeAsync(
        string id,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await subscriptions.ResumeAsync(id, userId, cancellationToken)),
            logger);
    }

    /// <summary>What the change would cost, before it is committed.</summary>
    [Authorize]
    [WolverinePost("/api/v1/subscriptions/{id}/preview-change")]
    public static async Task<IResult> PreviewChangeAsync(
        string id,
        ChangePlanRequest request,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(
                await subscriptions.PreviewChangeAsync(id, request.PlanName, userId, cancellationToken)),
            logger);
    }

    /// <summary>Moves the subscription onto another plan.</summary>
    [Authorize]
    [WolverinePost("/api/v1/subscriptions/{id}/change")]
    public static async Task<(IResult, PlanUpgradedNotification?)> ChangeAsync(
        string id,
        ChangePlanRequest request,
        [NotBody] SubscriptionCheckoutService subscriptions,
        [NotBody] TimeProvider clock,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<SubscriptionEndpoint> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return (BillingProblems.Unauthenticated(), null);

        PlanUpgradedNotification? notice = null;

        var result = await BillingProblems.GuardAsync(
            async () =>
            {
                // Read first, because "did this go up" is a comparison and the change is
                // destructive of the answer.
                var before = await subscriptions.GetAsync(id, userId, cancellationToken);
                var after = await subscriptions.ChangeAsync(id, request.PlanName, userId, cancellationToken);

                notice = BillingNotices.ForPlanChange(userId, before, after, clock.GetUtcNow());

                return Results.Ok(after);
            },
            logger);

        return (result, notice);
    }
}
