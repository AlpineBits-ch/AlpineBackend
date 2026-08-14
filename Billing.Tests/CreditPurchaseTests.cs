using Billing.Application.Credit;
using Billing.Application.Services;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Spending credit (monetization.md section 8.3), against a real Postgres.</summary>
[TestFixture]
public class CreditPurchaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private CreditLedgerService _ledger = null!;
    private CreditPurchaseService _purchases = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        await CreditFixtures.SeedPricedPlanAsync(_db, Plans.Pro, 999, "eur");
        await CreditFixtures.SeedPricedPlanAsync(_db, Plans.Plus, 499, "eur");

        _clock = new TestClock(Now);
        _ledger = CreditFixtures.Ledger(_db, _clock);
        _purchases = CreditFixtures.Purchases(_db, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private Task GiveAsync(long amount, string key = "seed") =>
        _ledger.IssueAsync(
            new IssueCredit(CreditFixtures.Buyer, amount, "Seeded.", key), CancellationToken.None);

    private Task<CreditPurchase> BuyProAsync(string key) =>
        _purchases.PurchaseAsync(
            CreditFixtures.Buyer, CreditFixtures.ProSku, CreditFixtures.Guild, key,
            CancellationToken.None);

    // ── the happy path ───────────────────────────────────────────────────────

    [Test]
    public async Task A_purchase_deducts_and_produces_a_credit_sourced_grant()
    {
        await GiveAsync(1_000);

        var purchase = await BuyProAsync("buy-1");

        var grant = await _db.Grants.SingleAsync();
        var spends = await _db.CreditEntries
            .Where(entry => entry.Kind == CreditEntryKind.Spend)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(purchase.BalanceAfter, Is.EqualTo(500));
            Assert.That(grant.Source, Is.EqualTo(GrantSource.Credit));
            Assert.That(grant.Plan, Is.EqualTo(Plans.Pro));
            Assert.That(grant.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(grant.SubjectId, Is.EqualTo(CreditFixtures.Guild));
            Assert.That(grant.StartsAt, Is.Null, "the first purchase starts now");
            Assert.That(grant.ExpiresAt, Is.EqualTo(Now.AddDays(30)));

            // The spend names the grant it bought, which is what makes the ledger answer "what did
            // this credit turn into".
            Assert.That(spends, Has.Count.EqualTo(1));
            Assert.That(spends[0].GrantId, Is.EqualTo(grant.Id));
            Assert.That(purchase.Announcement, Is.Not.Null);
        });
    }

    [Test]
    public async Task A_user_scoped_sku_lands_on_the_buyer_rather_than_a_guild()
    {
        await GiveAsync(1_000);

        await _purchases.PurchaseAsync(
            CreditFixtures.Buyer, CreditFixtures.UserPlusSku, CreditFixtures.Guild, "buy-1",
            CancellationToken.None);

        var grant = await _db.Grants.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(grant.SubjectKind, Is.EqualTo(SubjectKind.User));
            Assert.That(grant.SubjectId, Is.EqualTo(CreditFixtures.Buyer));
        });
    }

    [Test]
    public async Task A_purchase_past_the_balance_is_refused_and_creates_no_grant()
    {
        await GiveAsync(100);

        Assert.That(async () => await BuyProAsync("buy-1"),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.InsufficientBalance));

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.Grants.CountAsync(), Is.Zero);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(100));
        });
    }

    [Test]
    public async Task A_retried_purchase_buys_once_and_answers_with_the_first_grant()
    {
        await GiveAsync(1_000);

        var first = await BuyProAsync("buy-1");
        var second = await BuyProAsync("buy-1");

        Assert.Multiple(async () =>
        {
            Assert.That(second.WasReplay, Is.True);
            Assert.That(second.Grant.Id, Is.EqualTo(first.Grant.Id));
            Assert.That(second.Announcement, Is.Null, "a replay announces nothing");
            Assert.That(await _db.Grants.CountAsync(), Is.EqualTo(1));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));
        });
    }

    // ── queueing ─────────────────────────────────────────────────────────────

    /// <summary>The headline of section 8.3.</summary>
    [Test]
    public async Task A_second_purchase_of_the_same_plan_starts_when_the_first_one_ends()
    {
        await GiveAsync(2_000);

        var first = await BuyProAsync("buy-1");
        var second = await BuyProAsync("buy-2");

        Assert.Multiple(() =>
        {
            Assert.That(first.WasQueued, Is.False);
            Assert.That(second.WasQueued, Is.True);
            Assert.That(second.Grant.StartsAt, Is.EqualTo(Now.AddDays(30)));
            Assert.That(second.Grant.ExpiresAt, Is.EqualTo(Now.AddDays(60)));
        });
    }

    /// <summary>Queueing is behind whatever the subject already holds, not behind credit purchases
    /// only. A staff grant that swallowed a purchase would be the same "I spent my credit and got
    /// nothing" ticket by a different route.</summary>
    [Test]
    public async Task A_purchase_queues_behind_a_staff_grant_of_the_same_plan()
    {
        await GiveAsync(1_000);

        var grants = CreditFixtures.Grants(_db, _clock);
        await grants.IssueAsync(
            new IssueGrant(
                SubjectKind.Guild, CreditFixtures.Guild, GrantKind.Plan, Plans.Pro, null,
                Now.AddDays(45), "Compensation.", GrantSource.Staff),
            CreditFixtures.Staff,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        var purchase = await BuyProAsync("buy-1");

        Assert.Multiple(() =>
        {
            Assert.That(purchase.Grant.StartsAt, Is.EqualTo(Now.AddDays(45)));
            Assert.That(purchase.Grant.ExpiresAt, Is.EqualTo(Now.AddDays(75)));
        });
    }

    /// <summary>A different plan does not queue: the resolver merges the two by taking the more
    /// generous value, so thirty days of Plus alongside Pro is a real thing to hold even though it
    /// contributes nothing while Pro is live.</summary>
    [Test]
    public async Task A_purchase_of_a_different_plan_starts_immediately()
    {
        await GiveAsync(2_000);

        await BuyProAsync("buy-1");

        var plus = await _purchases.PurchaseAsync(
            CreditFixtures.Buyer, CreditFixtures.PlusSku, CreditFixtures.Guild, "buy-2",
            CancellationToken.None);

        Assert.That(plus.Grant.StartsAt, Is.Null);
    }

    /// <summary>The one case queueing cannot fix.</summary>
    [Test]
    public async Task A_purchase_behind_a_permanent_grant_of_the_same_plan_is_refused()
    {
        await GiveAsync(1_000);

        var grants = CreditFixtures.Grants(_db, _clock);
        await grants.IssueAsync(
            new IssueGrant(
                SubjectKind.Guild, CreditFixtures.Guild, GrantKind.Plan, Plans.Pro, null,
                null, "Early supporter, forever.", GrantSource.Staff),
            CreditFixtures.Staff,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.That(async () => await BuyProAsync("buy-1"),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditPurchaseErrorCodes.AlreadyPermanent));

        Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            Is.EqualTo(1_000));
    }

    /// <summary>A revoked grant is not something to queue behind.</summary>
    [Test]
    public async Task A_purchase_does_not_queue_behind_a_revoked_grant()
    {
        await GiveAsync(1_000);

        var grants = CreditFixtures.Grants(_db, _clock);
        var (existing, _) = await grants.IssueAsync(
            new IssueGrant(
                SubjectKind.Guild, CreditFixtures.Guild, GrantKind.Plan, Plans.Pro, null,
                Now.AddDays(45), "Compensation.", GrantSource.Staff),
            CreditFixtures.Staff,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        await grants.RevokeAsync(existing.Id, CreditFixtures.Staff, "Issued in error.", CancellationToken.None);
        await _db.SaveChangesAsync();

        var purchase = await BuyProAsync("buy-1");

        Assert.That(purchase.Grant.StartsAt, Is.Null);
    }

    // ── the resolver's view of a queued grant ────────────────────────────────

    /// <summary>
    /// The question the package had to answer before adding a field: does the resolver already
    /// handle a future-dated grant?
    /// </summary>
    [Test]
    public async Task A_queued_grant_is_invisible_to_the_resolver_until_its_day()
    {
        await GiveAsync(2_000);

        await BuyProAsync("buy-1");
        var queued = await BuyProAsync("buy-2");

        var grants = CreditFixtures.Grants(_db, _clock);
        var subject = EntitlementSubject.ForGuild(CreditFixtures.Guild);

        var today = await grants.ActiveGrantsAsync(subject, CancellationToken.None);

        _clock.MoveTo(Now.AddDays(31));
        var later = await grants.ActiveGrantsAsync(subject, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(today.Select(grant => grant.GrantId), Does.Not.Contain(queued.Grant.Id));
            Assert.That(today, Has.Count.EqualTo(1));

            Assert.That(later.Select(grant => grant.GrantId), Does.Contain(queued.Grant.Id));
            Assert.That(later, Has.Count.EqualTo(1), "and the first one has expired by then");
        });
    }

    /// <summary>The second lock on the same door: even handed a queued grant directly, the source
    /// contributes nothing from it. That matters because the provider's answer is cached.</summary>
    [Test]
    public void The_grant_source_ignores_a_grant_that_has_not_started()
    {
        var clock = new TestClock(Now);

        var source = new GrantEntitlementSource(
            new StubGrantProvider(new EntitlementGrant(
                "gran_queued", Plans.Pro, null, Now.AddDays(60), Now.AddDays(30))),
            Plans.Catalogue(),
            clock);

        var before = source.ResolveAsync(Subjects.Guild, CancellationToken.None).Result;

        clock.MoveTo(Now.AddDays(31));
        var after = source.ResolveAsync(Subjects.Guild, CancellationToken.None).Result;

        Assert.Multiple(() =>
        {
            Assert.That(before.Contains(GrantFixtures.Participants), Is.False);
            Assert.That(after.Contains(GrantFixtures.Participants), Is.True);
            Assert.That(after.Number(GrantFixtures.Participants), Is.EqualTo(50));
        });
    }

    // ── the section 8.1 gate ─────────────────────────────────────────────────

    /// <summary>
    /// Every SKU purchasable with credit must also have a plain cash price (section 8.1).
    /// </summary>
    [Test]
    public async Task A_sku_whose_plan_has_no_cash_price_is_neither_listed_nor_purchasable()
    {
        await CreditFixtures.SeedPricedPlanAsync(_db, "unpriced", priceMinorUnits: null, currency: null);
        await GiveAsync(1_000);

        var catalogue = CreditFixtures.Catalogue(_db);
        var offered = await catalogue.PurchasableAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(offered.Select(sku => sku.Code), Does.Not.Contain(CreditFixtures.UnpricedSku));

            Assert.That(
                async () => await _purchases.PurchaseAsync(
                    CreditFixtures.Buyer, CreditFixtures.UnpricedSku, CreditFixtures.Guild, "buy-1",
                    CancellationToken.None),
                Throws.InstanceOf<CreditRefusedException>()
                    .With.Property(nameof(CreditRefusedException.Code))
                    .EqualTo(CreditPurchaseErrorCodes.NoCashPrice));
        });

        Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            Is.EqualTo(1_000));
    }

    /// <summary>Both prices ride the catalogue together, so nobody has to guess what a credit is
    /// worth (section 8.2). The peg between them stays internal and is deliberately not on the
    /// wire.</summary>
    [Test]
    public async Task The_catalogue_shows_the_cash_price_beside_the_point_price()
    {
        var catalogue = CreditFixtures.Catalogue(_db);
        var pro = (await catalogue.PurchasableAsync(CancellationToken.None))
            .Single(sku => sku.Code == CreditFixtures.ProSku);

        Assert.Multiple(() =>
        {
            Assert.That(pro.PricePoints, Is.EqualTo(500));
            Assert.That(pro.CashPriceMinorUnits, Is.EqualTo(999));
            Assert.That(pro.CashCurrency, Is.EqualTo("eur"));
        });
    }

    /// <summary>An instance whose plans exist only in configuration has no prices at all, so it
    /// offers nothing for credit. Conservative rather than broken: selling something for credit that
    /// money cannot buy is the one thing section 8.1 rules out.</summary>
    [Test]
    public async Task An_instance_with_no_plan_rows_offers_nothing_for_credit()
    {
        // Versions before plans: the foreign key is Restrict, the same rule the rest of this context
        // follows so a plan somebody is on cannot be deleted out from under them.
        await _db.PlanVersions.ExecuteDeleteAsync();
        await _db.Plans.ExecuteDeleteAsync();
        _db.ChangeTracker.Clear();

        var catalogue = CreditFixtures.Catalogue(_db);

        Assert.That(await catalogue.PurchasableAsync(CancellationToken.None), Is.Empty);
    }

    // ── atomicity ────────────────────────────────────────────────────────────

    /// <summary>
    /// The wave gate: a deduction whose grant creation fails rolls back completely.
    /// </summary>
    [Test]
    public async Task A_purchase_whose_grant_is_refused_leaves_no_entries_and_no_balance_change()
    {
        await SeedArchivedButPricedPlanAsync("unpriced");
        await GiveAsync(1_000);

        var before = await _db.CreditEntries.CountAsync();

        Assert.That(
            async () => await _purchases.PurchaseAsync(
                CreditFixtures.Buyer, CreditFixtures.UnpricedSku, CreditFixtures.Guild, "buy-1",
                CancellationToken.None),
            Throws.InstanceOf<GrantRefusedException>());

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(before),
                "the deduction was rolled back with the grant");
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Spend), Is.Zero);
            Assert.That(await _db.Grants.CountAsync(), Is.Zero);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(1_000));
        });
    }

    /// <summary>
    /// The same property one level down, without a grant involved: a deduction inside a transaction
    /// that never commits leaves the ledger exactly as it was.
    /// </summary>
    [Test]
    public async Task A_deduction_inside_an_abandoned_transaction_writes_nothing()
    {
        await GiveAsync(1_000);

        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            await _ledger.DeductAsync(
                CreditFixtures.Buyer, 400, "spend-1", null, null, CancellationToken.None);

            await transaction.RollbackAsync();
        }

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(1_000));
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Spend), Is.Zero);
        });
    }

    /// <summary>A plan whose current version carries a price and has been archived.</summary>
    private async Task SeedArchivedButPricedPlanAsync(string name)
    {
        await CreditFixtures.SeedPricedPlanAsync(_db, name, 799, "eur");

        var plan = await _db.Plans.SingleAsync(row => row.Name == name);

        await _db.PlanVersions
            .Where(version => version.PlanId == plan.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(version => version.ArchivedAt, Now)
                .SetProperty(version => version.ArchivedBy, CreditFixtures.Staff)
                .SetProperty(version => version.ArchiveReason, "Withdrawn."));

        _db.ChangeTracker.Clear();
    }
}
