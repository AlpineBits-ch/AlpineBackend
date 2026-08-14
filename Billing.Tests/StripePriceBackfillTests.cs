using AppEnvironment;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Billing.Tests;

/// <summary>The startup backfill that makes a seeded catalogue buyable.</summary>
[TestFixture]
public class StripePriceBackfillTests
{
    private const string SecretKey = "sk_test_billingtests";

    private string _originalSecretKey = string.Empty;
    private MicroserviceContext _db = null!;
    private IStripeGateway _gateway = null!;
    private StripeCatalogueSync _sync = null!;
    private int _priceCounter;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

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

        _sync = new StripeCatalogueSync(_db, _gateway);
    }

    [TearDown]
    public async Task Dispose()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        await _db.DisposeAsync();
    }

    /// <summary>A plan exactly as <see cref="PlanSeeder"/> leaves it: priced, current, and with no
    /// Stripe price id at all.</summary>
    private async Task<(Plan Plan, PlanVersion Version)> SeedAsync(
        string name = "pro",
        long? price = 2900,
        string? currency = "usd",
        bool archivedPlan = false,
        bool archivedVersion = false)
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = name,
            DisplayName = name.ToUpperInvariant(),
            CurrentVersionNumber = 1,
            SeededFromConfiguration = true,
            CreatedBy = PlanSeeder.SystemActor,
            ArchivedAt = archivedPlan ? DateTimeOffset.UtcNow : null,
            ArchivedBy = archivedPlan ? "user_admin" : null,
            ArchiveReason = archivedPlan ? "Withdrawn." : null,
        };

        var version = new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = "{\"voice.max_participants\":\"75\"}",
            PriceMinorUnits = price,
            Currency = currency,
            Reason = PlanSeeder.SeedReason,
            CreatedBy = PlanSeeder.SystemActor,
            ArchivedAt = archivedVersion ? DateTimeOffset.UtcNow : null,
            ArchivedBy = archivedVersion ? "user_admin" : null,
            ArchiveReason = archivedVersion ? "Superseded." : null,
        };

        _db.Plans.Add(plan);
        _db.PlanVersions.Add(version);
        await _db.SaveChangesAsync();

        return (plan, version);
    }

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task A_seeded_priced_plan_gets_a_Stripe_price_and_becomes_buyable()
    {
        await SeedAsync();

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        var version = await _db.PlanVersions.AsNoTracking().SingleAsync();
        var plan = await _db.Plans.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.EqualTo(1));
            Assert.That(version.StripePriceId, Is.EqualTo("price_test_1"));
            Assert.That(plan.StripeProductId, Is.EqualTo("prod_test"));
        });
    }

    [Test]
    public async Task Every_priced_plan_in_a_seeded_catalogue_is_mirrored_in_one_pass()
    {
        await SeedAsync("plus", 900);
        await SeedAsync("pro", 2900);
        await SeedAsync("venta_plus", 600);
        await SeedAsync("free", price: null, currency: null);

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        var priced = await _db.PlanVersions.AsNoTracking()
            .Where(version => version.PriceMinorUnits != null)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.EqualTo(3));
            Assert.That(priced.All(version => version.StripePriceId != null), Is.True);
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>Second start, nothing to do.</summary>
    [Test]
    public async Task A_second_run_calls_Stripe_not_at_all()
    {
        await SeedAsync();
        await StripePriceBackfill.RunAsync(_db, _sync, null);

        _gateway.ClearReceivedCalls();

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task An_unpriced_plan_is_left_alone()
    {
        await SeedAsync("free", price: null, currency: null);

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    /// <summary>An archived plan is not sold, so mirroring one would create a Stripe price for
    /// something the catalogue has stopped publishing.</summary>
    [Test]
    public async Task An_archived_plan_is_not_mirrored()
    {
        await SeedAsync(archivedPlan: true);

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task An_archived_version_is_not_mirrored()
    {
        await SeedAsync(archivedVersion: true);

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        Assert.That(mirrored, Is.Zero);
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>A hosted instance mid-rollout has no key and must start exactly as it did before this
    /// existed.</summary>
    [Test]
    public async Task An_instance_with_no_secret_key_does_nothing()
    {
        Env.License.StripeSecretKey = string.Empty;
        await SeedAsync();

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    /// <summary>Stripe being unreachable at startup is not a reason to refuse to start.</summary>
    [Test]
    public async Task A_Stripe_failure_leaves_the_rest_of_the_catalogue_mirrored()
    {
        await SeedAsync("plus", 900);
        await SeedAsync("pro", 2900);

        var calls = 0;

        _gateway.CreatePriceAsync(
                Arg.Any<StripePriceRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new StripeGatewayException("prices.create", "Stripe is down.")
                : Task.FromResult(new StripeObjectRef("price_after_the_failure")));

        var mirrored = await StripePriceBackfill.RunAsync(_db, _sync, null);

        var versions = await _db.PlanVersions.AsNoTracking().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.EqualTo(1));
            Assert.That(versions.Count(version => version.StripePriceId != null), Is.EqualTo(1));
        });
    }
}
