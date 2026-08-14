using System.Security.Claims;
using Billing.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>The caller's own cards and invoices.</summary>
public class PaymentMethodEndpoint
{
    [Authorize]
    [WolverineGet("/api/v1/payment-methods")]
    public static async Task<IResult> ListAsync(
        [NotBody] PaymentMethodService cards,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PaymentMethodEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await cards.ListAsync(userId, cancellationToken)), logger);
    }

    [Authorize]
    [WolverinePost("/api/v1/payment-methods/setup-intent")]
    public static async Task<IResult> SetupIntentAsync(
        [NotBody] PaymentMethodService cards,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PaymentMethodEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await cards.CreateSetupIntentAsync(userId, cancellationToken)), logger);
    }

    [Authorize]
    [WolverinePost("/api/v1/payment-methods/{id}/default")]
    public static async Task<IResult> SetDefaultAsync(
        string id,
        [NotBody] PaymentMethodService cards,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PaymentMethodEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () =>
            {
                await cards.SetDefaultAsync(userId, id, cancellationToken);
                return Results.NoContent();
            },
            logger);
    }

    [Authorize]
    [WolverineDelete("/api/v1/payment-methods/{id}")]
    public static async Task<IResult> DetachAsync(
        string id,
        [NotBody] PaymentMethodService cards,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PaymentMethodEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () =>
            {
                await cards.DetachAsync(userId, id, cancellationToken);
                return Results.NoContent();
            },
            logger);
    }

    /// <summary>The caller's invoices, as links.</summary>
    [Authorize]
    [WolverineGet("/api/v1/invoices")]
    public static async Task<IResult> InvoicesAsync(
        [NotBody] PaymentMethodService cards,
        [NotBody] ClaimsPrincipal caller,
        [NotBody] ILogger<PaymentMethodEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var userId = BillingProblems.CallerId(caller);
        if (userId is null) return BillingProblems.Unauthenticated();

        return await BillingProblems.GuardAsync(
            async () => Results.Ok(await cards.ListInvoicesAsync(userId, cancellationToken)), logger);
    }
}
