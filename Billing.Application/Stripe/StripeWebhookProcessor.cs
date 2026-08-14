using System.Data.Common;
using AppEnvironment;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StripeSdk = Stripe;

namespace Billing.Application.Stripe;

/// <summary>The six event types the webhook destination is subscribed to, in one place so the
/// dispatch below and the operator's Stripe configuration cannot drift apart. Section 5.</summary>
public static class StripeEventTypes
{
    public const string SubscriptionCreated = "customer.subscription.created";
    public const string SubscriptionUpdated = "customer.subscription.updated";
    public const string SubscriptionDeleted = "customer.subscription.deleted";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoicePaymentFailed = "invoice.payment_failed";
    public const string ChargeDisputeCreated = "charge.dispute.created";

    public static readonly IReadOnlyList<string> All =
    [
        SubscriptionCreated, SubscriptionUpdated, SubscriptionDeleted,
        InvoicePaid, InvoicePaymentFailed, ChargeDisputeCreated,
    ];
}

/// <summary>What verification concluded.</summary>
public sealed record StripeWebhookVerification(
    StripeSdk.Event? Event,
    int RefusedWith,
    string? Refusal)
{
    public static StripeWebhookVerification Accepted(StripeSdk.Event stripeEvent) =>
        new(stripeEvent, 0, null);

    public static StripeWebhookVerification Refused(int status, string refusal) =>
        new(null, status, refusal);

    public bool IsAccepted => Event is not null;
}

/// <summary>The status and body to answer a delivery with, plus anything that has to be announced
/// on the bus.</summary>
public sealed record StripeWebhookResponse(
    int StatusCode,
    string Body,
    IReadOnlyList<EntitlementsChanged> Announcements)
{
    public static StripeWebhookResponse Ok(string body) => new(StatusCodes.Status200OK, body, []);
}

/// <summary>
/// Everything that happens to a Stripe delivery between the socket and the reconciler: is the
/// endpoint allowed to trust anything at all, is this delivery genuine, have we seen it before, and
/// which subscription is it about.
/// </summary>
public sealed class StripeWebhookProcessor(
    MicroserviceContext db,
    SubscriptionReconciler reconciler,
    TimeProvider clock,
    ILogger<StripeWebhookProcessor>? logger = null)
{
    /// <summary>The header the signature travels in.</summary>
    public const string SignatureHeader = "Stripe-Signature";

    /// <summary>
    /// Stripe.net's own pinned API version and the account's webhook API version are allowed to
    /// differ, and this integration does not care that they do.
    /// </summary>
    private const bool ThrowOnApiVersionMismatch = false;

    /// <summary>Whether this delivery is genuine, without touching the database.</summary>
    public static StripeWebhookVerification Verify(string? payload, string? signatureHeader)
    {
        if (!Env.License.IsStripeWebhookConfigured)
        {
            return StripeWebhookVerification.Refused(
                StatusCodes.Status503ServiceUnavailable,
                "STRIPE_WEBHOOK_SECRET is not set on this instance, so a delivery cannot be "
                + "authenticated and is refused. An unverified webhook is an endpoint through which "
                + "anybody on the internet grants themselves a subscription.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return StripeWebhookVerification.Refused(
                StatusCodes.Status400BadRequest, "An empty body carries no signature to verify.");
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return StripeWebhookVerification.Refused(
                StatusCodes.Status400BadRequest, $"No {SignatureHeader} header.");
        }

        try
        {
            var stripeEvent = StripeSdk.EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                Env.License.StripeWebhookSecret,
                throwOnApiVersionMismatch: ThrowOnApiVersionMismatch);

            return StripeWebhookVerification.Accepted(stripeEvent);
        }
        catch (StripeSdk.StripeException exception)
        {
            return StripeWebhookVerification.Refused(
                StatusCodes.Status400BadRequest, $"Signature verification failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The whole delivery: verify, claim the event id, reconcile, record what happened.
    /// </summary>
    public async Task<StripeWebhookResponse> HandleAsync(
        string? payload, string? signatureHeader, CancellationToken cancellationToken)
    {
        var verification = Verify(payload, signatureHeader);

        if (!verification.IsAccepted)
        {
            if (verification.RefusedWith == StatusCodes.Status503ServiceUnavailable)
            {
                logger?.LogError(
                    "A Stripe webhook delivery was refused because STRIPE_WEBHOOK_SECRET is not set. "
                    + "Every delivery will be refused until it is, which means subscriptions will "
                    + "never activate. Set it from the webhook destination in the Stripe dashboard; "
                    + "there is deliberately no compiled-in default for this one.");
            }
            else
            {
                logger?.LogWarning(
                    "A Stripe webhook delivery was refused: {Refusal}", verification.Refusal);
            }

            return new StripeWebhookResponse(
                verification.RefusedWith, verification.Refusal ?? string.Empty, []);
        }

        var stripeEvent = verification.Event!;
        var eventId = stripeEvent.Id;
        var eventType = stripeEvent.Type ?? "unknown";

        if (string.IsNullOrWhiteSpace(eventId))
        {
            logger?.LogWarning("A verified Stripe delivery of type {Type} carried no event id.", eventType);

            return StripeWebhookResponse.Ok("No event id; nothing recorded.");
        }

        // The fast path for the ordinary repeat, which is most duplicates: Stripe redelivers
        // anything it did not get a 2xx for quickly enough.
        if (await db.ProcessedStripeEvents.AnyAsync(row => row.EventId == eventId, cancellationToken))
        {
            logger?.LogDebug("Stripe event {Event} ({Type}) has already been handled.", eventId, eventType);

            return StripeWebhookResponse.Ok("Already handled.");
        }

        var now = clock.GetUtcNow();

        var claim = new ProcessedStripeEvent
        {
            EventId = eventId,
            Type = eventType,
            ReceivedAt = now,
        };

        db.ProcessedStripeEvents.Add(claim);

        try
        {
            var reconciliation = await DispatchAsync(stripeEvent, eventType, cancellationToken);

            claim.ProcessedAt = clock.GetUtcNow();
            claim.Outcome = Truncate(reconciliation.Outcome);

            return new StripeWebhookResponse(
                StatusCodes.Status200OK, reconciliation.Outcome, reconciliation.Announcements);
        }
        catch (Exception exception) when (!IsTransient(exception))
        {
            // Answered 200 on purpose.
            logger?.LogError(exception,
                "Stripe event {Event} ({Type}) was verified and could not be processed. Answering 200 "
                + "so Stripe stops retrying; the reason is on the processed_stripe_events row.",
                eventId, eventType);

            Discard();

            db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
            {
                EventId = eventId,
                Type = eventType,
                ReceivedAt = now,
                ProcessedAt = clock.GetUtcNow(),
                Outcome = Truncate($"Failed: {exception.Message}"),
            });

            return StripeWebhookResponse.Ok($"Recorded, not applied: {exception.Message}");
        }
    }

    /// <summary>Which subscription this event is about, and what it says about money.</summary>
    private async Task<SubscriptionReconciliation> DispatchAsync(
        StripeSdk.Event stripeEvent, string eventType, CancellationToken cancellationToken)
    {
        var eventAt = new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc));

        switch (eventType)
        {
            case StripeEventTypes.SubscriptionCreated:
            case StripeEventTypes.SubscriptionUpdated:
            case StripeEventTypes.SubscriptionDeleted:
            {
                var subscriptionId = SubscriptionIdOf(stripeEvent);

                return subscriptionId is null
                    ? SubscriptionReconciliation.Nothing(
                        $"{eventType}: the payload named no subscription.")
                    : await reconciler.ReconcileAsync(
                        subscriptionId, StripeDunningSignal.None, eventType, eventAt, cancellationToken);
            }

            case StripeEventTypes.InvoicePaid:
            case StripeEventTypes.InvoicePaymentFailed:
            {
                var subscriptionId = SubscriptionIdOf(stripeEvent);

                if (subscriptionId is null)
                {
                    // A one-off invoice with no subscription behind it is a normal object in a Stripe
                    // account and has nothing to reconcile.
                    return SubscriptionReconciliation.Nothing(
                        $"{eventType}: the invoice is not attached to a subscription.");
                }

                var signal = eventType == StripeEventTypes.InvoicePaid
                    ? StripeDunningSignal.PaymentSucceeded
                    : StripeDunningSignal.PaymentFailed;

                return await reconciler.ReconcileAsync(
                    subscriptionId, signal, eventType, eventAt, cancellationToken);
            }

            case StripeEventTypes.ChargeDisputeCreated:
                return Dispute(stripeEvent);

            default:
                return SubscriptionReconciliation.Nothing($"{eventType}: not an event this service acts on.");
        }
    }

    /// <summary>A dispute is recorded and alerted on, and changes nothing.</summary>
    private SubscriptionReconciliation Dispute(StripeSdk.Event stripeEvent)
    {
        var dispute = stripeEvent.Data?.Object as StripeSdk.Dispute;

        logger?.LogError(
            "A Stripe dispute was opened on charge {Charge} for {Amount} {Currency} ({Reason}). "
            + "Nothing was downgraded: a dispute is a human decision, because automating it cancels "
            + "paying customers over cards their bank flagged in error. Stripe event {Event}.",
            dispute?.ChargeId, dispute?.Amount, dispute?.Currency, dispute?.Reason, stripeEvent.Id);

        return SubscriptionReconciliation.Nothing(
            $"Dispute opened on charge {dispute?.ChargeId ?? "unknown"} ({dispute?.Reason ?? "no reason given"}). "
            + "Recorded and alerted; no entitlement changed.");
    }

    /// <summary>The one thing taken out of a payload.</summary>
    public static string? SubscriptionIdOf(StripeSdk.Event stripeEvent)
    {
        ArgumentNullException.ThrowIfNull(stripeEvent);

        var id = stripeEvent.Data?.Object switch
        {
            StripeSdk.Subscription subscription => subscription.Id,
            StripeSdk.Invoice invoice => invoice.Parent?.SubscriptionDetails?.SubscriptionId,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>Whether redelivering this would help.</summary>
    public static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is StripeGatewayException
                or DbException
                or DbUpdateException
                or TimeoutException
                or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Throws away everything a failed reconcile tracked, so the only row that commits is the one
    /// recording the failure.
    /// </summary>
    private void Discard()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    /// <summary>The outcome is free text for a person to read, not a payload.</summary>
    private static string Truncate(string outcome) =>
        outcome.Length <= 500 ? outcome : outcome[..500];
}
