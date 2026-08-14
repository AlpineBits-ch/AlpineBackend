using AppEnvironment;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Billing.Tests;

/// <summary>Mirroring a plan version into Stripe, against a substituted gateway.</summary>
[TestFixture]
public class StripeCatalogueSyncTests
{
    private const string SecretKey = "sk_test_billingtests";

    private string _originalSecretKey = string.Empty;
    private IStripeGateway _gateway = null!;
    private MicroserviceContext _db = null!;
    private StripeCatalogueSync _sync = null!;
    private int _priceCounter;

    [SetUp]
    public void SetUp()
    {
        _originalSecretKey = Env.License.StripeSecretKey;
        Env.License.StripeSecretKey = SecretKey;

        _priceCounter = 0;
        _gateway = Substitute.For<IStripeGateway>();

        _gateway.CreateProductAsync(
                Arg.Any<StripeProductRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StripeObjectRef("prod_test")));

        _gateway.CreatePriceAsync(
                Arg.Any<StripePriceRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StripeObjectRef($"price_test_{++_priceCounter}")));

        // Never connected to.
        _db = new MicroserviceContext(new DbContextOptionsBuilder<MicroserviceContext>().Options);
        _sync = new StripeCatalogueSync(_db, _gateway);
    }

    [TearDown]
    public void TearDown()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        _db.Dispose();
    }

    private static Plan NewPlan(string? productId = null) => new()
    {
        Id = "plan_01JQZZZZZZZZZZZZZZZZZZZZZZ",
        Name = "pro",
        DisplayName = "Pro",
        Description = "For communities that stream.",
        CurrentVersionNumber = 1,
        CreatedBy = "user_admin",
        StripeProductId = productId,
    };

    private static PlanVersion NewVersion(
        string planId, int number = 1, long? price = 2900, string? currency = "usd") => new()
    {
        Id = PlanVersion.GenerateId(),
        PlanId = planId,
        VersionNumber = number,
        ValuesJson = "{}",
        PriceMinorUnits = price,
        Currency = currency,
        Reason = "The launch tier.",
        CreatedBy = "user_admin",
    };

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task A_priced_version_creates_the_product_then_the_price_and_stores_both_ids()
    {
        var plan = NewPlan();
        var version = NewVersion(plan.Id);

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.Synced));
            Assert.That(plan.StripeProductId, Is.EqualTo("prod_test"));
            Assert.That(version.StripePriceId, Is.EqualTo("price_test_1"));
        });

        // The order is not cosmetic: a price needs a product to hang off, so a price created first
        // would fail against the real Stripe and pass against any substitute that did not check.
        Received.InOrder(() =>
        {
            _gateway.CreateProductAsync(
                Arg.Any<StripeProductRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
            _gateway.CreatePriceAsync(
                Arg.Any<StripePriceRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
        });
    }

    /// <summary>Both directions of identity.</summary>
    [Test]
    public async Task Our_ids_go_into_the_metadata_on_both_objects()
    {
        var plan = NewPlan();
        var version = NewVersion(plan.Id, number: 4);

        await _sync.SyncAsync(plan, version, CancellationToken.None);

        var product = (StripeProductRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateProductAsync))
            .GetArguments()[0]!;

        var price = (StripePriceRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreatePriceAsync))
            .GetArguments()[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(product.Metadata[StripeCatalogueSync.PlanMetadataKey], Is.EqualTo("pro"));
            Assert.That(product.Metadata[StripeCatalogueSync.PlanIdMetadataKey], Is.EqualTo(plan.Id));
            Assert.That(price.Metadata[StripeCatalogueSync.PlanMetadataKey], Is.EqualTo("pro"));
            Assert.That(price.Metadata[StripeCatalogueSync.PlanIdMetadataKey], Is.EqualTo(plan.Id));
            Assert.That(price.Metadata[StripeCatalogueSync.PlanVersionMetadataKey], Is.EqualTo("4"));

            Assert.That(price.UnitAmountMinorUnits, Is.EqualTo(2900));
            Assert.That(price.Currency, Is.EqualTo("usd"));
            Assert.That(price.Interval, Is.EqualTo("month"));
            Assert.That(price.ProductId, Is.EqualTo("prod_test"));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_plan_that_already_has_a_product_creates_only_the_price()
    {
        var plan = NewPlan(productId: "prod_existing");
        var version = NewVersion(plan.Id, number: 2);

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        await _gateway.DidNotReceive().CreateProductAsync(
            Arg.Any<StripeProductRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.Synced));
            Assert.That(plan.StripeProductId, Is.EqualTo("prod_existing"));
            Assert.That(version.StripePriceId, Is.EqualTo("price_test_1"));
        });
    }

    /// <summary>The grandfathering property, on the Stripe side.</summary>
    [Test]
    public async Task A_second_version_creates_a_second_price_and_leaves_the_first_alone()
    {
        var plan = NewPlan();
        var first = NewVersion(plan.Id, number: 1, price: 2900);

        await _sync.SyncAsync(plan, first, CancellationToken.None);

        var second = NewVersion(plan.Id, number: 2, price: 3900);
        var outcome = await _sync.SyncAsync(plan, second, CancellationToken.None);

        // One product across both, since the plan is the same product.
        await _gateway.Received(1).CreateProductAsync(
            Arg.Any<StripeProductRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());

        await _gateway.Received(2).CreatePriceAsync(
            Arg.Any<StripePriceRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.Synced));
            Assert.That(first.StripePriceId, Is.EqualTo("price_test_1"));
            Assert.That(second.StripePriceId, Is.EqualTo("price_test_2"));
        });
    }

    [Test]
    public async Task A_version_that_is_already_mirrored_is_not_mirrored_again()
    {
        var plan = NewPlan(productId: "prod_existing");
        var version = NewVersion(plan.Id);
        version.StripePriceId = "price_already_there";

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.AlreadySynced));
            Assert.That(version.StripePriceId, Is.EqualTo("price_already_there"));
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>Free and free_user are plans nobody buys and they still have to exist, so an unpriced
    /// version is a normal state rather than an error.</summary>
    [Test]
    public async Task An_unpriced_version_does_not_call_Stripe_at_all()
    {
        var plan = NewPlan();
        var version = NewVersion(plan.Id, price: null, currency: null);

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.NotPriced));
            Assert.That(plan.StripeProductId, Is.Null);
            Assert.That(version.StripePriceId, Is.Null);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task A_priced_version_with_no_currency_does_not_call_Stripe()
    {
        var plan = NewPlan();
        var version = NewVersion(plan.Id, price: 2900, currency: null);

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.NotPriced));
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    /// <summary>A hosted instance that has not finished setting Stripe up must publish plans exactly
    /// as it did before, rather than failing. Section 10 of the architecture document.</summary>
    [Test]
    public async Task An_instance_with_no_secret_key_does_not_call_Stripe_at_all()
    {
        Env.License.StripeSecretKey = string.Empty;

        var plan = NewPlan();
        var version = NewVersion(plan.Id);

        var outcome = await _sync.SyncAsync(plan, version, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(StripeSyncOutcome.NotConfigured));
            Assert.That(plan.StripeProductId, Is.Null);
            Assert.That(version.StripePriceId, Is.Null);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    // ── The idempotency keys, verbatim ───────────────────────────────────────

    /// <summary>The one test whose failure is otherwise invisible.</summary>
    [Test]
    public async Task The_idempotency_keys_are_exactly_the_documented_strings()
    {
        var plan = NewPlan();
        var version = NewVersion(plan.Id, number: 3);

        await _sync.SyncAsync(plan, version, CancellationToken.None);

        var productKey = (StripeIdempotencyKey)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateProductAsync))
            .GetArguments()[1]!;

        var priceKey = (StripeIdempotencyKey)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreatePriceAsync))
            .GetArguments()[1]!;

        Assert.Multiple(() =>
        {
            Assert.That(productKey.Value,
                Is.EqualTo("venta:product:plan_01JQZZZZZZZZZZZZZZZZZZZZZZ"));

            Assert.That(priceKey.Value,
                Is.EqualTo("venta:price:plan_01JQZZZZZZZZZZZZZZZZZZZZZZ:v3:usd:month"));
        });
    }

    [Test]
    public void A_price_key_distinguishes_currency_and_interval()
    {
        var usd = StripeIdempotencyKey.ForPrice("plan_x", 1, "usd", "month");
        var eur = StripeIdempotencyKey.ForPrice("plan_x", 1, "eur", "month");
        var annual = StripeIdempotencyKey.ForPrice("plan_x", 1, "usd", "year");

        // The same version priced in a second currency, or billed annually, is a different Stripe
        // Price.
        Assert.Multiple(() =>
        {
            Assert.That(usd.Value, Is.EqualTo("venta:price:plan_x:v1:usd:month"));
            Assert.That(eur.Value, Is.Not.EqualTo(usd.Value));
            Assert.That(annual.Value, Is.Not.EqualTo(usd.Value));
        });
    }

    [Test]
    public void A_key_cannot_be_built_from_nothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => StripeIdempotencyKey.ForProduct(" "),
                Throws.InstanceOf<ArgumentException>());

            Assert.That(() => StripeIdempotencyKey.ForPrice("plan_x", 1, "", "month"),
                Throws.InstanceOf<ArgumentException>());
        });
    }
}
