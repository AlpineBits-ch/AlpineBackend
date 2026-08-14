using AppEnvironment;
using Billing.Application.Stripe;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using StripeSdk = Stripe;

namespace Billing.Tests;

/// <summary>
/// The gate in front of the webhook: is this instance allowed to trust anything, and is this
/// delivery genuine.
/// </summary>
[TestFixture]
public class StripeWebhookVerificationTests
{
    private string _originalSecret = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _originalSecret = Env.License.StripeWebhookSecret;
        Env.License.StripeWebhookSecret = StripeWebhookFixtures.WebhookSecret;
    }

    [TearDown]
    public void TearDown() => Env.License.StripeWebhookSecret = _originalSecret;

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void A_correctly_signed_delivery_is_accepted()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_accepted");

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.Multiple(() =>
        {
            Assert.That(verification.IsAccepted, Is.True, verification.Refusal);
            Assert.That(verification.Event!.Id, Is.EqualTo("evt_accepted"));
            Assert.That(verification.Event.Type, Is.EqualTo(StripeEventTypes.SubscriptionUpdated));
        });
    }

    /// <summary>The mismatch that would otherwise take the integration down.</summary>
    [Test]
    public void A_delivery_rendered_at_a_different_api_version_is_still_accepted()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_version");

        Assert.That(StripeSdk.StripeConfiguration.ApiVersion, Is.Not.EqualTo(StripeWebhookFixtures.ApiVersion),
            "the fixture has to differ from the SDK or this test proves nothing");

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(verification.IsAccepted, Is.True, verification.Refusal);
    }

    [Test]
    public void The_subscription_id_is_read_off_a_subscription_payload()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_sub", subscriptionId: "sub_specific");

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(StripeWebhookProcessor.SubscriptionIdOf(verification.Event!), Is.EqualTo("sub_specific"));
    }

    /// <summary>An invoice carries its subscription under <c>parent.subscription_details</c>, which
    /// moved there in the 2025-03-31 API version. Reading the wrong place would make every dunning
    /// event a no-op that logs nothing interesting.</summary>
    [Test]
    public void The_subscription_id_is_read_off_an_invoice_payload()
    {
        var payload = StripeWebhookFixtures.InvoiceEvent(
            "evt_invoice", StripeEventTypes.InvoicePaid, subscriptionId: "sub_from_invoice");

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(StripeWebhookProcessor.SubscriptionIdOf(verification.Event!), Is.EqualTo("sub_from_invoice"));
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public void A_one_off_invoice_names_no_subscription()
    {
        var payload = StripeWebhookFixtures.StandaloneInvoiceEvent(
            "evt_standalone", StripeEventTypes.InvoicePaid);

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(StripeWebhookProcessor.SubscriptionIdOf(verification.Event!), Is.Null);
    }

    [Test]
    public void A_dispute_payload_names_no_subscription()
    {
        var payload = StripeWebhookFixtures.DisputeEvent("evt_dispute");

        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(StripeWebhookProcessor.SubscriptionIdOf(verification.Event!), Is.Null);
    }

    /// <summary>The six the destination is subscribed to, asserted as strings.</summary>
    [Test]
    public void The_subscribed_event_types_are_exactly_the_documented_strings()
    {
        Assert.That(StripeEventTypes.All, Is.EquivalentTo(new[]
        {
            "customer.subscription.created",
            "customer.subscription.updated",
            "customer.subscription.deleted",
            "invoice.paid",
            "invoice.payment_failed",
            "charge.dispute.created",
        }));
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>The security property of the whole package.</summary>
    [Test]
    public void A_missing_webhook_secret_refuses_the_delivery()
    {
        Env.License.StripeWebhookSecret = string.Empty;

        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_nosecret");

        // Signed with what would have been the right secret, so the only reason to refuse is that
        // this instance has nothing to verify against.
        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.Multiple(() =>
        {
            Assert.That(verification.IsAccepted, Is.False);
            Assert.That(verification.Event, Is.Null);
            Assert.That(verification.RefusedWith, Is.EqualTo(503));
            Assert.That(verification.Refusal, Does.Contain("STRIPE_WEBHOOK_SECRET"));
        });
    }

    [Test]
    public void Whitespace_is_not_a_webhook_secret_either()
    {
        Env.License.StripeWebhookSecret = "   ";

        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_whitespace");
        var verification = StripeWebhookProcessor.Verify(payload, StripeWebhookFixtures.Sign(payload));

        Assert.That(verification.RefusedWith, Is.EqualTo(503));
    }

    [Test]
    public void A_delivery_signed_with_the_wrong_secret_is_refused()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_forged");

        var forged = StripeSdk.EventUtility.GenerateSignatureHeader(payload, "whsec_somebodyelses");

        var verification = StripeWebhookProcessor.Verify(payload, forged);

        Assert.Multiple(() =>
        {
            Assert.That(verification.IsAccepted, Is.False);
            Assert.That(verification.RefusedWith, Is.EqualTo(400),
                "a forged delivery will never become valid, so Stripe must not be told to retry it");
        });
    }

    /// <summary>The signature covers the exact bytes.</summary>
    [Test]
    public void A_delivery_whose_body_changed_after_signing_is_refused()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_tampered");
        var signature = StripeWebhookFixtures.Sign(payload);

        var altered = payload.Replace("\"status\": \"active\"", "\"status\":\"active\"", StringComparison.Ordinal);

        Assert.That(altered, Is.Not.EqualTo(payload), "the fixture has to actually change");

        Assert.That(StripeWebhookProcessor.Verify(altered, signature).IsAccepted, Is.False);
    }

    [Test]
    public void An_unsigned_delivery_is_refused()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_unsigned");

        Assert.Multiple(() =>
        {
            Assert.That(StripeWebhookProcessor.Verify(payload, null).RefusedWith, Is.EqualTo(400));
            Assert.That(StripeWebhookProcessor.Verify(payload, "  ").RefusedWith, Is.EqualTo(400));
            Assert.That(StripeWebhookProcessor.Verify(payload, "t=1,v1=deadbeef").RefusedWith, Is.EqualTo(400));
        });
    }

    [Test]
    public void An_empty_body_is_refused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StripeWebhookProcessor.Verify(null, "t=1,v1=deadbeef").RefusedWith, Is.EqualTo(400));
            Assert.That(StripeWebhookProcessor.Verify(string.Empty, "t=1,v1=deadbeef").RefusedWith, Is.EqualTo(400));
        });
    }

    // ── The 200-versus-5xx line ──────────────────────────────────────────────

    /// <summary>Which failures are worth redelivering.</summary>
    [Test]
    public void Only_failures_redelivery_would_fix_are_transient()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StripeWebhookProcessor.IsTransient(
                new StripeGatewayException("subscriptions.get", "Stripe is down")), Is.True);

            Assert.That(StripeWebhookProcessor.IsTransient(
                new DbUpdateException("the database went away")), Is.True);

            Assert.That(StripeWebhookProcessor.IsTransient(new TimeoutException()), Is.True);

            Assert.That(StripeWebhookProcessor.IsTransient(new OperationCanceledException()), Is.True);

            // Wrapped, because that is how they actually arrive.
            Assert.That(StripeWebhookProcessor.IsTransient(
                new InvalidOperationException("outer", new TimeoutException())), Is.True);

            // A conclusion rather than an interruption: the fourth attempt reaches it too.
            Assert.That(StripeWebhookProcessor.IsTransient(
                new InvalidOperationException("no such plan version")), Is.False);

            Assert.That(StripeWebhookProcessor.IsTransient(new NullReferenceException()), Is.False);

            Assert.That(StripeWebhookProcessor.IsTransient(null), Is.False);
        });
    }
}
