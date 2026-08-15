using Billing.Application.Credit;
using Billing.Application.Stripe;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Billing.Tests;

/// <summary>
/// Renewing from credit instead of from a card, against a real Postgres and a substituted gateway.
/// </summary>
[TestFixture]
public class CreditRenewalSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Two days out, so it sits inside the three-day default lead and a test that widens or
    /// narrows the window has somewhere to move it to.</summary>
    private static readonly DateTimeOffset PeriodEnd = Now.AddDays(2);

    private const string SubscriptionId = "sub_credit_renewal";

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private CreditLedgerService _ledger = null!;
    private CreditPurchaseService _purchases = null!;
    private CreditCatalogueService _catalogue = null!;
    private IStripeGateway _gateway = null!;
    private CreditOptions _options = null!;
    private string _proPlanId = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        await CreditFixtures.SeedPricedPlanAsync(_db, Plans.Pro, 2900, "usd");
        await CreditFixtures.SeedPricedPlanAsync(_db, Plans.Plus, 900, "usd");

        _proPlanId = (await _db.Plans.SingleAsync(plan => plan.Name == Plans.Pro)).Id;

        _clock = new TestClock(Now);
        _options = CreditFixtures.Options();
        _ledger = CreditFixtures.Ledger(_db, _clock, _options);
        _purchases = CreditFixtures.Purchases(_db, _clock, _options);
        _catalogue = CreditFixtures.Catalogue(_db, _options);

        _gateway = Substitute.For<IStripeGateway>();
        DeferralReturns(PeriodEnd.AddDays(30));
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    // ── the happy path ───────────────────────────────────────────────────────

    [Test]
    public async Task Credit_buys_the_next_period_and_the_card_stands_down()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        var announcements = await SweepAsync();

        var grant = await _db.Grants.SingleAsync();
        var spend = await _db.CreditEntries.SingleAsync(entry => entry.Kind == CreditEntryKind.Spend);

        Assert.Multiple(async () =>
        {
            Assert.That(grant.Source, Is.EqualTo(GrantSource.Credit));
            Assert.That(grant.Plan, Is.EqualTo(Plans.Pro));
            Assert.That(grant.SubjectId, Is.EqualTo(CreditFixtures.Guild));

            // The whole point of the lead window: bought two days early, still starting on the day the
            // paid period actually ends. A grant starting now would finish two days short of it.
            Assert.That(grant.StartsAt, Is.EqualTo(PeriodEnd));
            Assert.That(grant.ExpiresAt, Is.EqualTo(PeriodEnd.AddDays(30)));

            Assert.That(spend.Amount, Is.EqualTo(-500));
            Assert.That(spend.GrantId, Is.EqualTo(grant.Id));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));

            Assert.That(announcements, Has.Count.EqualTo(1));
        });

        // The card is told to stand down for exactly as long as the grant runs, which is what makes
        // this a substitution for the charge rather than a gift on top of one.
        await _gateway.Received(1).DeferBillingAsync(
            SubscriptionId,
            PeriodEnd.AddDays(30),
            Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task The_new_period_end_is_mirrored_so_the_next_pass_skips_it()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        await SweepAsync();

        _db.ChangeTracker.Clear();
        var row = await _db.Subscriptions.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.CurrentPeriodEnd, Is.EqualTo(PeriodEnd.AddDays(30)));

            // Stripe reports free time as a trial, which SubscriptionStatuses.Live already counts, so
            // nothing downstream needs a new status to keep the plan on.
            Assert.That(row.Status, Is.EqualTo(SubscriptionStatus.Trialing));
            Assert.That(SubscriptionStatuses.IsLive(row.Status), Is.True);
        });
    }

    // ── the two consents ─────────────────────────────────────────────────────

    [Test]
    public async Task A_subscription_that_did_not_opt_in_is_left_alone()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync(useCredit: false);

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    [Test]
    public async Task The_operator_switch_stops_the_sweep_without_touching_any_subscription()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        // Read on every pass rather than at registration, so an operator watching something spend
        // wallets it should not be can stop it without editing anybody's subscription row.
        _options.RenewFromCredit = false;

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    // ── the refusals ─────────────────────────────────────────────────────────

    [Test]
    public async Task A_wallet_that_cannot_cover_a_whole_period_is_not_part_spent()
    {
        // Fifty points against a five-hundred-point SKU.
        await GiveAsync(50);
        await SeedSubscriptionAsync();

        await SweepAsync();

        await AssertNothingHappenedAsync(50);
    }

    [Test]
    public async Task A_subscription_already_scheduled_to_end_is_not_renewed()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync(cancelAtPeriodEnd: true);

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    [Test]
    public async Task A_past_due_subscription_is_not_renewed_from_credit()
    {
        // Money is already owed on an invoice Stripe is retrying.
        await GiveAsync(1_000);
        await SeedSubscriptionAsync(status: SubscriptionStatus.PastDue);

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    [Test]
    public async Task A_renewal_beyond_the_lead_window_waits()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync(periodEnd: Now.AddDays(20));

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    [Test]
    public async Task A_period_end_already_in_the_past_is_treated_as_a_stale_mirror()
    {
        // Active with an elapsed period end means this row is behind Stripe, not that a renewal is
        // due. Acting on it would buy a second month for a period that was already invoiced.
        await GiveAsync(1_000);
        await SeedSubscriptionAsync(periodEnd: Now.AddDays(-1));

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    [Test]
    public async Task An_instance_with_no_catalogue_spends_nothing()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        _options.Catalogue.Clear();

        await SweepAsync();

        await AssertNothingHappenedAsync(1_000);
    }

    // ── the two failure directions ───────────────────────────────────────────

    [Test]
    public async Task A_stripe_refusal_rolls_the_deduction_back_and_the_card_is_charged_as_normal()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        _gateway.DeferBillingAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<StripeIdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Throws(new StripeGatewayException("subscriptions.update.defer_billing", "Stripe said no."));

        var announcements = await SweepAsync();

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            // Neither half landed.
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(1_000));
            Assert.That(await _db.Grants.CountAsync(), Is.Zero);
            Assert.That(await _db.CreditEntries.CountAsync(entry => entry.Kind == CreditEntryKind.Spend),
                Is.Zero);
            Assert.That(await _db.Subscriptions.Select(row => row.CurrentPeriodEnd).SingleAsync(),
                Is.EqualTo(PeriodEnd));
            Assert.That(announcements, Is.Empty);
        });
    }

    [Test]
    public async Task A_second_pass_over_the_same_period_replays_rather_than_buying_twice()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        // Stripe answering with the period end unchanged is what keeps the row in the window for a
        // second pass - the shape of a deferral that was accepted but whose answer was lost.
        DeferralReturns(PeriodEnd.AddDays(30), reportedPeriodEnd: PeriodEnd);

        await SweepAsync();
        _db.ChangeTracker.Clear();
        await SweepAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.CreditEntries.CountAsync(entry => entry.Kind == CreditEntryKind.Spend),
                Is.EqualTo(1));
            Assert.That(await _db.Grants.CountAsync(), Is.EqualTo(1));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));
        });
    }

    [Test]
    public async Task A_second_month_queues_behind_the_first_rather_than_overlapping_it()
    {
        await GiveAsync(1_000);
        await SeedSubscriptionAsync();

        await SweepAsync();

        // The next renewal, a month later: same subscription, new period, and a grant that has to
        // start where the last one ended rather than running concurrently and evaporating.
        _db.ChangeTracker.Clear();
        var secondPeriodEnd = PeriodEnd.AddDays(30);
        _clock.MoveTo(secondPeriodEnd.AddDays(-2));
        DeferralReturns(secondPeriodEnd.AddDays(30));

        await SweepAsync();

        var grants = await _db.Grants.OrderBy(grant => grant.StartsAt).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(grants, Has.Count.EqualTo(2));
            Assert.That(grants[1].StartsAt, Is.EqualTo(secondPeriodEnd));
            Assert.That(grants[1].ExpiresAt, Is.EqualTo(secondPeriodEnd.AddDays(30)));
        });
    }

    // ── which SKU renews what ────────────────────────────────────────────────

    [Test]
    public void The_sku_has_to_match_the_plan_and_the_subject()
    {
        var skus = new[]
        {
            Sku("guild.plus.30d", Plans.Plus, 250, 30, SubjectKind.Guild),
            Sku("user.plus.30d", Plans.Plus, 200, 30, SubjectKind.User),
        };

        Assert.Multiple(() =>
        {
            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Plus, SubjectKind.Guild, 1_000)?.Code,
                Is.EqualTo("guild.plus.30d"));

            // A wallet holding enough for a guild month must not renew somebody's personal plan with
            // it, and the cheaper user SKU is exactly what a plan-only match would have picked.
            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Plus, SubjectKind.User, 1_000)?.Code,
                Is.EqualTo("user.plus.30d"));

            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Pro, SubjectKind.Guild, 1_000), Is.Null);
        });
    }

    [Test]
    public void The_longest_affordable_sku_wins_and_a_tie_goes_to_the_cheaper_one()
    {
        var skus = new[]
        {
            Sku("guild.pro.30d", Plans.Pro, 500, 30, SubjectKind.Guild),
            Sku("guild.pro.90d", Plans.Pro, 1_400, 90, SubjectKind.Guild),
            Sku("guild.pro.30d.alt", Plans.Pro, 450, 30, SubjectKind.Guild),
        };

        Assert.Multiple(() =>
        {
            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Pro, SubjectKind.Guild, 2_000)?.Code,
                Is.EqualTo("guild.pro.90d"));

            // Not enough for the quarter, so the month - and the cheaper of the two months.
            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Pro, SubjectKind.Guild, 500)?.Code,
                Is.EqualTo("guild.pro.30d.alt"));

            Assert.That(CreditRenewalSweeper.Best(skus, Plans.Pro, SubjectKind.Guild, 100), Is.Null);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CreditSku Sku(
        string code, string plan, long points, int days, SubjectKind subject) =>
        new(code, code, null, points, plan, days, subject, 999, "usd");

    private Task GiveAsync(long amount) =>
        _ledger.IssueAsync(
            new IssueCredit(CreditFixtures.Buyer, amount, "Seeded.", "seed"), CancellationToken.None);

    private Task<IReadOnlyList<EntitlementsChanged>> SweepAsync() =>
        CreditRenewalSweeper.CollectAsync(
            _db, _ledger, _catalogue, _purchases, _gateway, _clock.GetUtcNow(), _options,
            batchSize: 100, logger: null, CancellationToken.None);

    /// <summary>Points the substituted gateway at what Stripe would answer.</summary>
    private void DeferralReturns(DateTimeOffset resumeAt, DateTimeOffset? reportedPeriodEnd = null) =>
        _gateway.DeferBillingAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<StripeIdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeSubscriptionSnapshot(
                SubscriptionId,
                "trialing",
                "cus_test",
                "price_test",
                reportedPeriodEnd ?? resumeAt,
                CancelAtPeriodEnd: false,
                LatestInvoiceId: null,
                Metadata: new Dictionary<string, string>())));

    private async Task SeedSubscriptionAsync(
        bool useCredit = true,
        bool cancelAtPeriodEnd = false,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? periodEnd = null)
    {
        _db.Subscriptions.Add(new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = SubscriptionId,
            PayerUserId = CreditFixtures.Buyer,
            SubjectKind = SubjectKind.Guild,
            SubjectId = CreditFixtures.Guild,
            PlanId = _proPlanId,
            VersionNumber = 1,
            Status = status,
            CurrentPeriodEnd = periodEnd ?? PeriodEnd,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
            UseCreditBeforeCharging = useCredit,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    /// <summary>No spend, no grant, and nothing said to Stripe.</summary>
    private async Task AssertNothingHappenedAsync(long expectedBalance)
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await _db.Grants.CountAsync(), Is.Zero);
            Assert.That(await _db.CreditEntries.CountAsync(entry => entry.Kind == CreditEntryKind.Spend),
                Is.Zero);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(expectedBalance));
        });

        await _gateway.DidNotReceive().DeferBillingAsync(
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }
}

