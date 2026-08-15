using Billing.Application.Bus.Consumers;
using Billing.Application.Credit;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Identity.Contracts.Bus.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Billing.Tests;

/// <summary>
/// The fraud void, fired by a ban rather than by a moderator remembering an endpoint
/// (monetization.md section 8.6, CP-06).
/// </summary>
[TestFixture]
public class CreditFraudVoidOnBanTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Moderator = "user_moderator";

    private MicroserviceContext _db = null!;
    private CreditLedgerService _ledger = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _ledger = CreditFixtures.Ledger(_db, new TestClock(Now));
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private Task<CreditLedgerResult> IssueAsync(long amount, string key) =>
        _ledger.IssueAsync(
            new IssueCredit(
                CreditFixtures.Buyer, amount, "Compensation for the outage on the 3rd.", key,
                Campaign: null, CreatedBy: CreditFixtures.Staff),
            CancellationToken.None);

    private Task HandleAsync(bool banned = true, string? userId = null, string? reason = "Ban: chargeback fraud") =>
        UserModerationStatusChangedHandler.Handle(
            new UserModerationStatusChangedEvent
            {
                UserId = userId ?? CreditFixtures.Buyer,
                Banned = banned,
                Status = banned ? "Banned" : "Active",
                PreviousStatus = banned ? "Active" : "Banned",
                ActorUserId = Moderator,
                Reason = reason,
                OccurredAt = Now,
            },
            _ledger,
            NullLogger<UserModerationStatusChangedHandler>.Instance,
            CancellationToken.None);

    // ── normal ──────────────────────────────────────────────────────────────

    /// <summary>The whole of section 8.6 in one assertion set: the balance goes to zero, and the
    /// record of what was issued and why is still there. An account banned in error should get its
    /// history back rather than a blank page.</summary>
    [Test]
    public async Task A_ban_voids_the_open_lots_and_leaves_every_entry_standing()
    {
        await IssueAsync(500, "issue-1");
        await IssueAsync(300, "issue-2");

        await HandleAsync();

        var entries = await _db.CreditEntries.OrderBy(entry => entry.Amount).ToListAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(0));
            Assert.That(await CreditFixtures.SumOfEntriesAsync(_db, CreditFixtures.Buyer), Is.EqualTo(0),
                "the cached balance and the entries have to agree");
            Assert.That((await _ledger.OpenLotsAsync(CreditFixtures.Buyer, CancellationToken.None)),
                Is.Empty);

            Assert.That(entries.Count(entry => entry.Kind == CreditEntryKind.Issue), Is.EqualTo(2),
                "nothing is ever deleted from the ledger");
            Assert.That(entries.Count(entry => entry.Kind == CreditEntryKind.Reversal), Is.EqualTo(2),
                "one reversal per outstanding lot");
            Assert.That(await _db.CreditLots.CountAsync(), Is.EqualTo(2), "the lots stay too");
        });
    }

    /// <summary>The reason travels from the moderation decision onto the reversal, because a voided
    /// balance has nowhere else to explain itself.</summary>
    [Test]
    public async Task The_bans_reason_is_written_onto_the_reversal()
    {
        await IssueAsync(500, "issue-1");

        await HandleAsync();

        var reversal = await _db.CreditEntries.SingleAsync(entry => entry.Kind == CreditEntryKind.Reversal);

        Assert.Multiple(() =>
        {
            Assert.That(reversal.Reason, Does.Contain("chargeback fraud"));
            Assert.That(reversal.CreatedBy, Is.EqualTo(Moderator),
                "the staff member who banned the account owns the void");
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    /// <summary>The outbox retries, so the same ban arrives more than once.</summary>
    [Test]
    public async Task A_replayed_ban_voids_nothing_the_second_time()
    {
        await IssueAsync(500, "issue-1");

        await HandleAsync();
        var afterFirst = await _db.CreditEntries.CountAsync();

        await HandleAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(afterFirst));
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(0));
        });
    }

    /// <summary>A ban with no reason on it still has to void.</summary>
    [Test]
    public async Task A_ban_with_no_reason_still_voids()
    {
        await IssueAsync(500, "issue-1");

        await HandleAsync(reason: null);

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(0));
            Assert.That(await _db.CreditEntries.AnyAsync(entry => entry.Kind == CreditEntryKind.Reversal),
                Is.True);
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    /// <summary>Most banned accounts have never been given credit.</summary>
    [Test]
    public async Task A_banned_account_with_no_wallet_is_a_no_op()
    {
        Assert.That(async () => await HandleAsync(userId: "user_never_had_credit"), Throws.Nothing);

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.CreditEntries.AnyAsync(), Is.False);
            Assert.That(await _db.CreditLots.AnyAsync(), Is.False);
        });
    }

    /// <summary>
    /// The void is one-way and the restore half of the event is ignored on purpose.
    /// </summary>
    [Test]
    public async Task An_unban_voids_nothing()
    {
        await IssueAsync(500, "issue-1");

        await HandleAsync(banned: false);

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
                Is.EqualTo(500));
            Assert.That(await _db.CreditEntries.CountAsync(), Is.EqualTo(1));
        });
    }

    /// <summary>And an unban after a void does not give the credit back, which is the reading somebody
    /// will eventually assume. Pinned here so changing it has to be a decision.</summary>
    [Test]
    public async Task An_unban_after_a_void_does_not_restore_the_balance()
    {
        await IssueAsync(500, "issue-1");
        await HandleAsync();

        await HandleAsync(banned: false);

        Assert.That(await _ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None), Is.EqualTo(0));
    }
}
