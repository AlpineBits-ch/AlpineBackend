using System.Security.Claims;
using Billing.Application.Dtos;
using Billing.Application.Services;
using Billing.Application.Stripe;

namespace Billing.Application.Endpoints;

/// <summary>One shape for every failure on the customer-facing billing surface.</summary>
public static class BillingProblems
{
    /// <summary>
    /// The caller's account id, or null for a token that carries no subject at all.
    /// </summary>
    public static string? CallerId(ClaimsPrincipal caller)
    {
        var id = caller?.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller?.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public static IResult Problem(string code, int statusCode, string message) =>
        Results.Problem(
            detail: message,
            statusCode: statusCode,
            title: "Billing",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult From(CheckoutRefusedException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return Problem(refusal.Code, refusal.StatusCode, refusal.Message);
    }

    public static IResult From(StripeGatewayException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return Problem(BillingErrorCodes.StripeError, StatusCodes.Status502BadGateway, failure.Message);
    }

    /// <summary>
    /// Runs an endpoint body and turns the two failures this surface has into problem responses.
    /// </summary>
    public static async Task<IResult> GuardAsync(Func<Task<IResult>> body, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            return await body();
        }
        catch (CheckoutRefusedException refusal)
        {
            return From(refusal);
        }
        catch (StripeGatewayException failure)
        {
            logger?.LogWarning(failure,
                "Stripe refused {Operation} on a customer billing request.", failure.Operation);

            return From(failure);
        }
    }

    /// <summary>The 401 for a token with no subject.</summary>
    public static IResult Unauthenticated() => Results.Unauthorized();
}
