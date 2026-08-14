using System.Text;
using Billing.Application.Stripe;
using Billing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Billing.Application.Endpoints;

/// <summary>Where Stripe posts.</summary>
public class StripeWebhookEndpoint
{
    /// <summary>The route inside this service, after the gateway has stripped its segment.</summary>
    public const string Route = "/api/v1/stripe/webhook";

    /// <summary>What the webhook destination in the Stripe dashboard points at.</summary>
    public const string PublicPath = "/api/v1/billing/stripe/webhook";

    [AllowAnonymous]
    [WolverinePost(Route)]
    public static async Task<(IResult, OutgoingMessages)> ReceiveAsync(
        [NotBody] StripeWebhookProcessor processor,
        [NotBody] HttpContext http,

        // Present so Wolverine's AutoApplyTransactions sees a DbContext on this chain and commits
        // what the processor tracked - it keys off the signature, and the processor's own injected
        // context is the same scoped instance.
        [NotBody] MicroserviceContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        string payload;

        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            payload = await reader.ReadToEndAsync(cancellationToken);
        }

        var signature = http.Request.Headers[StripeWebhookProcessor.SignatureHeader].ToString();

        var response = await processor.HandleAsync(payload, signature, cancellationToken);

        var messages = new OutgoingMessages();
        foreach (var announcement in response.Announcements) messages.Add(announcement);

        return (Results.Text(response.Body, "text/plain", Encoding.UTF8, response.StatusCode), messages);
    }
}
