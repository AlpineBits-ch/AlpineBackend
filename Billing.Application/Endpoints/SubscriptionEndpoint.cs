using System.Security.Claims;
using Billing.Application.Dtos;
using Billing.Application.Services;
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

    [Authorize]
    [WolverinePost("/api/v1/subscriptions/{id}/change")]
    public static async Task<IResult> ChangeAsync(
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
                await subscriptions.ChangeAsync(id, request.PlanName, userId, cancellationToken)),
            logger);
    }
}
