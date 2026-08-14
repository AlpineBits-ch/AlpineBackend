using Billing.Application.Credit;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// The wallet and its lot ledger (monetization.md section 8.5), against a real Postgres.
/// </summary>
[TestFixture]
public class CreditLedgerTests
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

    private Task<CreditLedgerResult> IssueAsync(
        long amount, string key, DateTimeOffset? expiresAt = null, string? campaign = null) =>
        _ledger.IssueAsync(
            new IssueCredit(
                CreditFixtures.Buyer, amount, "Compensation for the outage on the 3rd.", key,
                campaign, CreditFixtures.Staff, expiresAt),
            CancellationToken.None);

    // ── issuing ──────────────────────────────────────────────────────────────

    [Test]
    public async Task An_issue_creates_a_lot_an_entry_and_a_balance()
    {
        var result = await IssueAsync(500, "issue-1");

        var lot = await _db.CreditLots.SingleAsync();
        var entry = await _db.CreditEntries.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Balance, Is.EqualTo(500));
            Assert.That(lot.OriginalAmount, Is.EqualTo(500));
            Assert.That(lot.UserId, Is.EqualTo(CreditFixtures.Buyer));

            Assert.That(entry.Kind, Is.EqualTo(CreditEntryKind.Issue));
            Assert.That(entry.Amount, Is.EqualTo(500));
            Assert.That(entry.LotId, Is.EqualTo(lot.Id));
            Assert.That(entry.CreatedBy, Is.EqualTo(CreditFixtures.Staff));
        });
    }

    /// <summary>Twelve months by default (section 8.5), and per-lot rather than per-account, which is
    /// the property the whole expiry design rests on.</summary>
    [Test]
    public async Task A_lot_expires_twelve_months_out_by_default()
    {
        await IssueAsync(100, "issue-1");

        var lot = await _db.CreditLots.SingleAsync();

        Assert.That(lot.ExpiresAt, Is.EqualTo(Now.AddDays(365)));
    }

    [Test]
    public async Task An_issue_of_zero_or_less_is_refused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(async () => await IssueAsync(0, "issue-zero"),
                Throws.InstanceOf<CreditRefusedException>()
                    .With.Property(nameof(CreditRefusedException.Code))
                    .EqualTo(CreditErrorCodes.AmountNotPositive));

            Assert.That(async () => await IssueAsync(-5, "issue-negative"),
                Throws.InstanceOf<CreditRefusedException>());
        });
    }

    [Test]
    public void An_issue_without_a_reason_is_refused()
    {
        Assert.That(
            async () => await _ledger.IssueAsync(
                new IssueCredit(CreditFixtures.Buyer, 100, "  ", "issue-1"), CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.ReasonRequired));
    }

    /// <summary>Section 8.6.</summary>
    [Test]
    public async Task An_issue_past_the_wallet_cap_is_refused_and_writes_nothing()
    {
        var ledger = CreditFixtures.Ledger(_db, _clock, CreditFixtures.Options(walletCap: 1_000));

        await ledger.IssueAsync(
            new IssueCredit(CreditFixtures.Buyer, 900, "Goodwill.", "issue-1"), CancellationToken.None);

        Assert.That(
            async () => await ledger.IssueAsync(
                new IssueCredit(CreditFixtures.Buyer, 200, "More goodwill.", "issue-2"),
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.WalletCapExceeded));

        Assert.Multiple(async () =>
        {
            Assert.That(await ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(900));
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(1));
        });
    }

    // ── spending ─────────────────────────────────────────────────────────────

    /// <summary>The FIFO gate from the wave's verification list.</summary>
    [Test]
    public async Task A_spend_consumes_the_earliest_expiring_lot_first()
    {
        await IssueAsync(300, "late", expiresAt: Now.AddDays(300));
        await IssueAsync(300, "early", expiresAt: Now.AddDays(10));
        await IssueAsync(300, "middle", expiresAt: Now.AddDays(100));

        await _ledger.DeductAsync(
            CreditFixtures.Buyer, 250, "spend-1", null, null, CancellationToken.None);

        var lots = await _ledger.OpenLotsAsync(CreditFixtures.Buyer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lots.Select(lot => lot.ExpiresAt),
                Is.EqualTo(new[] { Now.AddDays(10), Now.AddDays(100), Now.AddDays(300) }));

            // The 10-day lot paid, not the 300-day one.
            Assert.That(lots[0].Remaining, Is.EqualTo(50));
            Assert.That(lots[1].Remaining, Is.EqualTo(300));
            Assert.That(lots[2].Remaining, Is.EqualTo(300));
        });
    }

    [Test]
    public async Task A_spend_that_crosses_two_lots_writes_one_entry_per_lot()
    {
        await IssueAsync(100, "early", expiresAt: Now.AddDays(10));
        await IssueAsync(100, "late", expiresAt: Now.AddDays(100));

        var result = await _ledger.DeductAsync(
            CreditFixtures.Buyer, 150, "spend-1", null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Entries, Has.Count.EqualTo(2));
            Assert.That(result.Entries.Select(entry => entry.Amount), Is.EqualTo(new[] { -100L, -50L }));
            Assert.That(result.Balance, Is.EqualTo(50));

            // Distinct keys, or the unique index would have refused the second row.
            Assert.That(result.Entries.Select(entry => entry.IdempotencyKey).Distinct().Count(),
                Is.EqualTo(2));
        });
    }

    [Test]
    public async Task A_spend_past_the_balance_is_refused()
    {
        await IssueAsync(100, "issue-1");

        Assert.That(
            async () => await _ledger.DeductAsync(
                CreditFixtures.Buyer, 101, "spend-1", null, null, CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.InsufficientBalance));

        Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            Is.EqualTo(100));
    }

    /// <summary>The retry gate from the wave's verification list, on the single-threaded path. The
    /// concurrent version is in <c>CreditConcurrencyTests</c>.</summary>
    [Test]
    public async Task A_retried_spend_does_not_deduct_twice()
    {
        await IssueAsync(500, "issue-1");

        var first = await _ledger.DeductAsync(
            CreditFixtures.Buyer, 200, "spend-1", null, null, CancellationToken.None);
        var second = await _ledger.DeductAsync(
            CreditFixtures.Buyer, 200, "spend-1", null, null, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(first.WasReplay, Is.False);
            Assert.That(second.WasReplay, Is.True);
            Assert.That(second.Balance, Is.EqualTo(300));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(300));
            Assert.That(await _db.CreditEntries.CountAsync(entry => entry.Kind == CreditEntryKind.Spend),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_retried_issue_does_not_credit_twice()
    {
        await IssueAsync(500, "issue-1");
        var again = await IssueAsync(500, "issue-1");

        Assert.Multiple(async () =>
        {
            Assert.That(again.WasReplay, Is.True);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));
            Assert.That(await _db.CreditLots.CountAsync(), Is.EqualTo(1));
        });
    }

    // ── the cache ────────────────────────────────────────────────────────────

    /// <summary>Section 8.5 allows a materialised balance only if it can always be rebuilt from the
    /// entries. This is the proof, over every kind of write the ledger performs.</summary>
    [Test]
    public async Task The_cached_balance_agrees_with_the_sum_of_entries_after_every_kind_of_write()
    {
        await IssueAsync(400, "issue-1", expiresAt: Now.AddDays(10));
        await IssueAsync(600, "issue-2", expiresAt: Now.AddDays(200));
        await _ledger.DeductAsync(CreditFixtures.Buyer, 500, "spend-1", null, null, CancellationToken.None);
        await _ledger.AdjustAsync(
            CreditFixtures.Buyer, 50, "Support goodwill.", CreditFixtures.Staff, "adjust-1",
            CancellationToken.None);
        await _ledger.AdjustAsync(
            CreditFixtures.Buyer, -30, "Typo in the last one.", CreditFixtures.Staff, "adjust-2",
            CancellationToken.None);

        var sum = await CreditFixtures.SumOfEntriesAsync(_db, CreditFixtures.Buyer);
        var cached = await _ledger.CachedBalanceAsync(CreditFixtures.Buyer, CancellationToken.None);
        var lots = await _ledger.OpenLotsAsync(CreditFixtures.Buyer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cached, Is.EqualTo(sum));

            // The other half of the invariant: every entry belongs to a lot, so the open lots add
            // up to the same number the entries do.
            Assert.That(lots.Sum(lot => lot.Remaining), Is.EqualTo(sum));
        });
    }

    [Test]
    public async Task A_corrupted_cache_is_repaired_by_a_rebuild()
    {
        await IssueAsync(700, "issue-1");

        await _db.CreditWallets
            .Where(wallet => wallet.UserId == CreditFixtures.Buyer)
            .ExecuteUpdateAsync(update => update.SetProperty(wallet => wallet.CachedBalance, 3));

        Assert.That(await _ledger.CachedBalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            Is.EqualTo(3));

        var rebuilt = await _ledger.RebuildAsync(CreditFixtures.Buyer, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(rebuilt, Is.EqualTo(700));
            Assert.That(await _ledger.CachedBalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(700));
        });
    }

    // ── reversal, and the constraint it protects ─────────────────────────────

    [Test]
    public async Task Reversing_an_issue_takes_the_balance_back_and_keeps_both_entries()
    {
        var issued = await IssueAsync(500, "issue-1");

        var reversal = await _ledger.ReverseEntryAsync(
            issued.Entries[0].Id, "Issued to the wrong account.", CreditFixtures.Staff,
            CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(reversal.Balance, Is.Zero);
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(2));
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Issue),
                Is.EqualTo(1));
            Assert.That(reversal.Entries[0].Reason, Is.EqualTo("Issued to the wrong account."));
        });
    }

    /// <summary>The section 8.6 constraint, asserted rather than left to a comment.</summary>
    [Test]
    public async Task A_spend_cannot_be_reversed()
    {
        await IssueAsync(500, "issue-1");
        var spend = await _ledger.DeductAsync(
            CreditFixtures.Buyer, 200, "spend-1", null, null, CancellationToken.None);

        Assert.That(
            async () => await _ledger.ReverseEntryAsync(
                spend.Entries[0].Id, "Customer changed their mind.", CreditFixtures.Staff,
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.NotReversible));

        Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            Is.EqualTo(300));
    }

    [Test]
    public async Task An_issue_that_has_already_been_spent_cannot_be_reversed()
    {
        var issued = await IssueAsync(500, "issue-1");
        await _ledger.DeductAsync(CreditFixtures.Buyer, 500, "spend-1", null, null, CancellationToken.None);

        Assert.That(
            async () => await _ledger.ReverseEntryAsync(
                issued.Entries[0].Id, "Should not have been issued.", CreditFixtures.Staff,
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.NothingToReverse));
    }

    [Test]
    public async Task Reversing_the_same_entry_twice_takes_the_credit_once()
    {
        var issued = await IssueAsync(500, "issue-1");

        await _ledger.ReverseEntryAsync(
            issued.Entries[0].Id, "Wrong account.", CreditFixtures.Staff, CancellationToken.None);

        var again = await _ledger.ReverseEntryAsync(
            issued.Entries[0].Id, "Wrong account.", CreditFixtures.Staff, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(again.WasReplay, Is.True);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.Zero);
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Reversal),
                Is.EqualTo(1));
        });
    }

    // ── the fraud void ───────────────────────────────────────────────────────

    /// <summary>The last of the wave's verification gates: a fraud ban voids outstanding lots and
    /// leaves the entries in place.</summary>
    [Test]
    public async Task A_fraud_void_zeroes_the_balance_and_keeps_every_entry()
    {
        await IssueAsync(400, "issue-1", expiresAt: Now.AddDays(10));
        await IssueAsync(600, "issue-2", expiresAt: Now.AddDays(200));
        await _ledger.DeductAsync(CreditFixtures.Buyer, 100, "spend-1", null, null, CancellationToken.None);

        var before = await _db.CreditEntries.CountAsync();

        var voided = await _ledger.VoidForFraudAsync(
            CreditFixtures.Buyer, "Banned for payment fraud, ticket 4412.", CreditFixtures.Staff,
            CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(voided.Balance, Is.Zero);
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.Zero);

            // Two lots still had something in them, so two reversals - and nothing was removed.
            Assert.That(voided.Entries, Has.Count.EqualTo(2));
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(before + 2));
            Assert.That(await _db.CreditEntries.CountAsync(e => e.Kind == CreditEntryKind.Issue),
                Is.EqualTo(2));
            Assert.That(await _db.CreditLots.CountAsync(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task A_second_fraud_void_writes_nothing()
    {
        await IssueAsync(400, "issue-1");
        await _ledger.VoidForFraudAsync(
            CreditFixtures.Buyer, "Fraud.", CreditFixtures.Staff, CancellationToken.None);

        var before = await _db.CreditEntries.CountAsync();

        await _ledger.VoidForFraudAsync(
            CreditFixtures.Buyer, "Fraud.", CreditFixtures.Staff, CancellationToken.None);

        Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(before));
    }

    // ── adjustments ──────────────────────────────────────────────────────────

    [Test]
    public async Task A_positive_adjustment_creates_a_lot_so_it_expires_like_anything_else()
    {
        await _ledger.AdjustAsync(
            CreditFixtures.Buyer, 250, "Support goodwill for ticket 991.", CreditFixtures.Staff,
            "adjust-1", CancellationToken.None);

        var lot = await _db.CreditLots.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lot.OriginalAmount, Is.EqualTo(250));
            Assert.That(lot.ExpiresAt, Is.EqualTo(Now.AddDays(365)));
        });
    }

    [Test]
    public async Task A_negative_adjustment_cannot_take_the_balance_below_zero()
    {
        await IssueAsync(100, "issue-1");

        Assert.That(
            async () => await _ledger.AdjustAsync(
                CreditFixtures.Buyer, -500, "Clawback.", CreditFixtures.Staff, "adjust-1",
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.InsufficientBalance));
    }

    [Test]
    public void An_adjustment_without_a_reason_is_refused()
    {
        Assert.That(
            async () => await _ledger.AdjustAsync(
                CreditFixtures.Buyer, 100, " ", CreditFixtures.Staff, "adjust-1", CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.ReasonRequired));
    }

    // ── scoping ──────────────────────────────────────────────────────────────

    /// <summary>Wallets are user-scoped (section 8.4).</summary>
    [Test]
    public async Task One_accounts_credit_is_not_another_accounts()
    {
        await IssueAsync(500, "issue-1");

        await _ledger.IssueAsync(
            new IssueCredit("user_somebody_else", 900, "Theirs.", "issue-2"), CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));
            Assert.That(await _ledger.BalanceAsync("user_somebody_else", CancellationToken.None),
                Is.EqualTo(900));
        });
    }
}
