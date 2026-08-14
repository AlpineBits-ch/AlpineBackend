using Billing.Application.Stripe;
using StripeSdk = Stripe;

namespace Billing.Tests.Helpers;

/// <summary>Genuine Stripe webhook bodies, signed the way Stripe signs them.</summary>
internal static class StripeWebhookFixtures
{
    public const string WebhookSecret = "whsec_billingtestswebhooksecret";

    /// <summary>The account's API version, which is not the SDK's. See the class comment.</summary>
    public const string ApiVersion = "2026-04-22.dahlia";

    public const string SubscriptionId = "sub_billingtests";

    public static string Sign(string payload) =>
        StripeSdk.EventUtility.GenerateSignatureHeader(payload, WebhookSecret);

    /// <summary>A subscription event.</summary>
    public static string SubscriptionEvent(
        string eventId,
        string type = StripeEventTypes.SubscriptionUpdated,
        string subscriptionId = SubscriptionId,
        string payloadStatus = "active",
        long created = 1_760_000_000) =>
        $$"""
          {
            "id": "{{eventId}}",
            "object": "event",
            "api_version": "{{ApiVersion}}",
            "created": {{created}},
            "livemode": false,
            "type": "{{type}}",
            "data": {
              "object": {
                "id": "{{subscriptionId}}",
                "object": "subscription",
                "status": "{{payloadStatus}}",
                "customer": "cus_billingtests"
              }
            }
          }
          """;

    /// <summary>An invoice event.</summary>
    public static string InvoiceEvent(
        string eventId,
        string type,
        string subscriptionId = SubscriptionId,
        long created = 1_760_000_000) =>
        $$"""
          {
            "id": "{{eventId}}",
            "object": "event",
            "api_version": "{{ApiVersion}}",
            "created": {{created}},
            "livemode": false,
            "type": "{{type}}",
            "data": {
              "object": {
                "id": "in_billingtests",
                "object": "invoice",
                "customer": "cus_billingtests",
                "parent": {
                  "type": "subscription_details",
                  "subscription_details": {
                    "subscription": "{{subscriptionId}}"
                  }
                }
              }
            }
          }
          """;

    /// <summary>A one-off invoice, with no subscription behind it.</summary>
    public static string StandaloneInvoiceEvent(string eventId, string type) =>
        $$"""
          {
            "id": "{{eventId}}",
            "object": "event",
            "api_version": "{{ApiVersion}}",
            "created": 1760000000,
            "livemode": false,
            "type": "{{type}}",
            "data": {
              "object": {
                "id": "in_standalone",
                "object": "invoice",
                "customer": "cus_billingtests"
              }
            }
          }
          """;

    public static string DisputeEvent(string eventId, string chargeId = "ch_billingtests") =>
        $$"""
          {
            "id": "{{eventId}}",
            "object": "event",
            "api_version": "{{ApiVersion}}",
            "created": 1760000000,
            "livemode": false,
            "type": "{{StripeEventTypes.ChargeDisputeCreated}}",
            "data": {
              "object": {
                "id": "dp_billingtests",
                "object": "dispute",
                "charge": "{{chargeId}}",
                "amount": 2900,
                "currency": "usd",
                "reason": "fraudulent",
                "status": "warning_needs_response"
              }
            }
          }
          """;

    /// <summary>An event type the destination is not subscribed to, which a shared endpoint can still
    /// receive - Stripe sends <c>ping</c> and an operator can widen the subscription by hand.</summary>
    public static string UnrelatedEvent(string eventId) =>
        $$"""
          {
            "id": "{{eventId}}",
            "object": "event",
            "api_version": "{{ApiVersion}}",
            "created": 1760000000,
            "livemode": false,
            "type": "customer.updated",
            "data": {
              "object": {
                "id": "cus_billingtests",
                "object": "customer"
              }
            }
          }
          """;
}
