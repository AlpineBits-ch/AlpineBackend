using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Wolverine;

namespace Billing.Tests;

/// <summary>What happens when Stripe will not compute the tax.</summary>
[TestFixture]
public class CheckoutTaxFallbackTests
{
    private const string Customer = "cus_test";
    private const string Price = "price_pro_v1";

    private IStripeGateway _gateway = null!;
    private MicroserviceContext _db = null!;
    private SubscriptionCheckoutService _checkout = null!;

    private static readonly EntitlementSubject Guild = EntitlementSubject.ForGuild("gld_test");

    private static readonly Dictionary<string, string> Metadata = new()
    {
        ["venta_subject_kind"] = "guild",
        ["venta_subject_id"] = "gld_test",
    };

    private static StripeSubscriptionResult Created(string id) =>
        new(new StripeSubscriptionSnapshot(
                id, "incomplete", Customer, Price, null, false, "in_1",
                new Dictionary<string, string>()),
            $"{id}_secret");

    [SetUp]
    public void SetUp()
    {
        _gateway = Substitute.For<IStripeGateway>();

        // Never connected to. The path under test reads and writes nothing.
        _db = new MicroserviceContext(new DbContextOptionsBuilder<MicroserviceContext>().Options);

        _checkout = new SubscriptionCheckoutService(
            _db,
            _gateway,
            new StripeCustomerRegistry(_db, _gateway),
            new EntitlementPlanOptions(),
            Substitute.For<IMessageBus>());
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Automatic_tax_is_on_when_Stripe_accepts_it()
    {
        _gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Created("sub_taxed")));

        var result = await _checkout.CreateWithTaxFallbackAsync(
            Customer, Price, Guild, Metadata, CancellationToken.None);

        var request = (StripeSubscriptionRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync))
            .GetArguments()[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Subscription.Id, Is.EqualTo("sub_taxed"));
            Assert.That(request.AutomaticTax, Is.True);
            Assert.That(request.Metadata, Is.EqualTo(Metadata));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_tax_refusal_is_retried_once_without_automatic_tax()
    {
        var attempts = 0;

        _gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;

                return attempts == 1
                    ? throw new StripeGatewayException(
                        "subscriptions.create",
                        "Stripe refused: we could not determine the customer's tax location.",
                        null,
                        "customer_tax_location_invalid")
                    : Task.FromResult(Created("sub_untaxed"));
            });

        var result = await _checkout.CreateWithTaxFallbackAsync(
            Customer, Price, Guild, Metadata, CancellationToken.None);

        var calls = _gateway.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync))
            .Select(call => call.GetArguments())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Subscription.Id, Is.EqualTo("sub_untaxed"));
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(((StripeSubscriptionRequest)calls[0][0]!).AutomaticTax, Is.True);
            Assert.That(((StripeSubscriptionRequest)calls[1][0]!).AutomaticTax, Is.False);

            // Different parameters mean a different request, and Stripe refuses a replayed key that
            // arrives with changed parameters - so the fallback presenting the first key would fail
            // with an error about the key rather than succeeding without tax.
            Assert.That(((StripeIdempotencyKey)calls[1][1]!).Value,
                Is.Not.EqualTo(((StripeIdempotencyKey)calls[0][1]!).Value));

            Assert.That(((StripeIdempotencyKey)calls[1][1]!).Value, Does.EndWith(":untaxed"));
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>A declined card is not a tax problem and must not be retried into a subscription with
    /// the tax quietly switched off.</summary>
    [Test]
    public void A_refusal_that_is_not_about_tax_is_not_retried()
    {
        _gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<StripeSubscriptionResult>>(_ => throw new StripeGatewayException(
                "subscriptions.create", "Your card was declined.", null, "card_declined"));

        Assert.That(
            async () => await _checkout.CreateWithTaxFallbackAsync(
                Customer, Price, Guild, Metadata, CancellationToken.None),
            Throws.InstanceOf<StripeGatewayException>());

        Assert.That(
            _gateway.ReceivedCalls().Count(
                call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync)),
            Is.EqualTo(1));
    }

    [Test]
    public void A_second_tax_refusal_is_surfaced_rather_than_retried_forever()
    {
        _gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<StripeSubscriptionResult>>(_ => throw new StripeGatewayException(
                "subscriptions.create", "Tax calculation failed.", null, "tax_calculation_failed"));

        Assert.That(
            async () => await _checkout.CreateWithTaxFallbackAsync(
                Customer, Price, Guild, Metadata, CancellationToken.None),
            Throws.InstanceOf<StripeGatewayException>());

        Assert.That(
            _gateway.ReceivedCalls().Count(
                call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync)),
            Is.EqualTo(2));
    }

    [Test]
    public void A_failure_with_no_Stripe_code_is_not_mistaken_for_a_tax_failure()
    {
        var failure = new StripeGatewayException("subscriptions.create", "Stripe timed out.");

        Assert.That(failure.IsTaxFailure, Is.False);
    }
}
