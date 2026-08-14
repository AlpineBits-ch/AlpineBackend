using Billing.Application.Credit;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// Lot expiry and the thirty-day warning (monetization.md section 8.5), against a real Postgres.
/// </summary>
[TestFixture]
public class CreditExpirySweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private CreditLedgerService _ledger = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _clock = new TestClock(Now);
        _ledger = CreditFixtures.Ledger(_db, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private Task GiveAsync(long amount, string key, DateTimeOffset expiresAt, string user = CreditFixtures.Buyer) =>
        _ledger.IssueAsync(
            new IssueCredit(user, amount, "Seeded.", key, ExpiresAt: expiresAt), CancellationToken.None);

    [Test]
    public async Task A_lapsed_lot_is_written_off_rather_than_deleted()
    {
        await GiveAsync(400, "old", Now.AddDays(5));
        await GiveAsync(600, "new", Now.AddDays(200));

        _clock.MoveTo(Now.AddDays(6));
        var written = await _ledger.ExpireLotsAsync(100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(written, Has.Count.EqualTo(1));
            Assert.That(written[0].Kind, Is.EqualTo(CreditEntryKind.Expiry));
            Assert.That(written[0].Amount, Is.EqualTo(-400));

            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(600));

            // Nothing removed: both lots and both issues are still there.
            Assert.That(await _db.CreditLots.CountAsync(), Is.EqualTo(2));
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Issue),
                Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Only_what_is_left_in_a_lot_expires()
    {
        await GiveAsync(500, "old", Now.AddDays(5));
        await _ledger.DeductAsync(CreditFixtures.Buyer, 300, "spend-1", null, null, CancellationToken.None);

        _clock.MoveTo(Now.AddDays(6));
        var written = await _ledger.ExpireLotsAsync(100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(written[0].Amount, Is.EqualTo(-200));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.Zero);
        });
    }

    /// <summary>The multi-replica property, and the reason the key is <c>expiry:{lot}</c> rather than
    /// a lookback window. A second pass over the same lot writes nothing.</summary>
    [Test]
    public async Task A_second_sweep_does_not_expire_the_same_lot_twice()
    {
        await GiveAsync(400, "old", Now.AddDays(5));

        _clock.MoveTo(Now.AddDays(6));
        await _ledger.ExpireLotsAsync(100, CancellationToken.None);
        var second = await _ledger.ExpireLotsAsync(100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(second, Is.Empty);
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Expiry),
                Is.EqualTo(1));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.Zero);
        });
    }

    [Test]
    public async Task A_lot_that_has_not_lapsed_is_left_alone()
    {
        await GiveAsync(400, "future", Now.AddDays(40));

        var written = await _ledger.ExpireLotsAsync(100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(written, Is.Empty);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(400));
        });
    }

    [Test]
    public async Task Expiring_lots_across_two_accounts_settles_both()
    {
        await GiveAsync(400, "a", Now.AddDays(5));
        await GiveAsync(700, "b", Now.AddDays(5), "user_other");

        _clock.MoveTo(Now.AddDays(6));
        var written = await _ledger.ExpireLotsAsync(100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(written, Has.Count.EqualTo(2));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.Zero);
            Assert.That(await _ledger.BalanceAsync("user_other", CancellationToken.None), Is.Zero);
        });
    }

    // ── the warning ──────────────────────────────────────────────────────────

    [Test]
    public async Task A_lot_inside_the_warning_window_is_claimed_and_announced()
    {
        await GiveAsync(400, "soon", Now.AddDays(20));
        await GiveAsync(600, "later", Now.AddDays(200));

        var warnings = await CreditExpirySweeper.CollectWarningsAsync(
            _ledger, _db, 100, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0].UserId, Is.EqualTo(CreditFixtures.Buyer));
            Assert.That(warnings[0].Points, Is.EqualTo(400));
            Assert.That(warnings[0].ExpiresAt, Is.EqualTo(Now.AddDays(20)));

            var claimed = await _db.CreditLots
                .Where(lot => lot.ExpiryWarningSentAt != null)
                .CountAsync();

            Assert.That(claimed, Is.EqualTo(1));
        });
    }

    /// <summary>One notification per lot, however many passes run.</summary>
    [Test]
    public async Task A_lot_is_only_warned_about_once()
    {
        await GiveAsync(400, "soon", Now.AddDays(20));

        await CreditExpirySweeper.CollectWarningsAsync(_ledger, _db, 100, CancellationToken.None);
        _db.ChangeTracker.Clear();
        var second = await CreditExpirySweeper.CollectWarningsAsync(
            _ledger, _db, 100, CancellationToken.None);

        Assert.That(second, Is.Empty);
    }

    [Test]
    public async Task An_empty_lot_inside_the_window_is_not_warned_about()
    {
        await GiveAsync(400, "soon", Now.AddDays(20));
        await _ledger.DeductAsync(CreditFixtures.Buyer, 400, "spend-1", null, null, CancellationToken.None);

        var warnings = await CreditExpirySweeper.CollectWarningsAsync(
            _ledger, _db, 100, CancellationToken.None);

        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public async Task A_lot_that_has_already_lapsed_is_not_warned_about()
    {
        await GiveAsync(400, "gone", Now.AddDays(5));

        _clock.MoveTo(Now.AddDays(6));

        var warnings = await CreditExpirySweeper.CollectWarningsAsync(
            _ledger, _db, 100, CancellationToken.None);

        Assert.That(warnings, Is.Empty);
    }

    /// <summary>The window is configuration, so an instance that wants sixty days' notice gets it
    /// without a code change.</summary>
    [Test]
    public async Task The_warning_window_comes_from_configuration()
    {
        await GiveAsync(400, "soon", Now.AddDays(45));

        var narrow = await CreditExpirySweeper.CollectWarningsAsync(
            _ledger, _db, 100, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var wide = CreditFixtures.Ledger(_db, _clock, CreditFixtures.Options(warningDays: 60));
        var claimed = await CreditExpirySweeper.CollectWarningsAsync(
            wide, _db, 100, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(narrow, Is.Empty);
            Assert.That(claimed, Has.Count.EqualTo(1));
        });
    }
}
