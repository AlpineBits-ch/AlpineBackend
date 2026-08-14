using Billing.Application.Credit;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// The concurrency half of monetization.md section 8.5, against real concurrent transactions on a
/// real Postgres.
/// </summary>
[TestFixture]
public class CreditConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>Seeds a balance through its own context, so nothing the test does afterwards shares a
    /// connection with the arrangement.</summary>
    private static async Task GiveAsync(long amount, string key, DateTimeOffset? expiresAt = null)
    {
        await using var db = PostgresTestDatabase.CreateContext();

        var ledger = CreditFixtures.Ledger(db, new TestClock(Now));

        await ledger.IssueAsync(
            new IssueCredit(CreditFixtures.Buyer, amount, "Seeded.", key, ExpiresAt: expiresAt),
            CancellationToken.None);
    }

    /// <summary>
    /// Runs several spends at once, each on its own connection, released together.
    /// </summary>
    private static async Task<IReadOnlyList<Exception?>> SpendTogetherAsync(
        int count, long each, Func<int, string> key)
    {
        using var gate = new SemaphoreSlim(0, count);
        var failures = new Exception?[count];

        var runs = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await using var db = PostgresTestDatabase.CreateContext();
            var ledger = CreditFixtures.Ledger(db, new TestClock(Now));

            await gate.WaitAsync();

            try
            {
                await ledger.DeductAsync(
                    CreditFixtures.Buyer, each, key(index), null, null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures[index] = exception;
            }
        })).ToArray();

        gate.Release(count);
        await Task.WhenAll(runs);

        return failures;
    }

    private static async Task<(long Balance, long SumOfEntries, int Spends)> StateAsync()
    {
        await using var db = PostgresTestDatabase.CreateContext();

        var ledger = CreditFixtures.Ledger(db, new TestClock(Now));

        return (
            await ledger.BalanceAsync(CreditFixtures.Buyer, CancellationToken.None),
            await CreditFixtures.SumOfEntriesAsync(db, CreditFixtures.Buyer),
            await db.CreditEntries.CountAsync(entry => entry.Kind == CreditEntryKind.Spend));
    }

    /// <summary>The wave gate: concurrent spends cannot overdraw.</summary>
    [Test]
    public async Task Two_concurrent_spends_for_the_whole_balance_leave_one_refused()
    {
        await GiveAsync(100, "seed");

        var failures = await SpendTogetherAsync(2, 100, index => $"spend-{index}");

        var (balance, sum, spends) = await StateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(balance, Is.Zero, "the whole balance went exactly once");
            Assert.That(sum, Is.EqualTo(balance), "the cache and the entries agree");
            Assert.That(spends, Is.EqualTo(1));

            Assert.That(failures.Count(failure => failure is null), Is.EqualTo(1));
            Assert.That(failures.Where(failure => failure is not null),
                Has.All.InstanceOf<CreditRefusedException>());
        });
    }

    /// <summary>
    /// The same property with more contenders and a balance that covers some of them, which is where
    /// an off-by-one in the lock shows up that two requests would hide.
    /// </summary>
    [Test]
    public async Task Six_concurrent_spends_against_a_balance_for_two_settle_at_exactly_two()
    {
        await GiveAsync(250, "seed");

        var failures = await SpendTogetherAsync(6, 100, index => $"spend-{index}");

        var (balance, sum, spends) = await StateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(balance, Is.EqualTo(50));
            Assert.That(balance, Is.GreaterThanOrEqualTo(0));
            Assert.That(sum, Is.EqualTo(balance));
            Assert.That(spends, Is.EqualTo(2));
            Assert.That(failures.Count(failure => failure is null), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The retry gate, run as an actual race rather than as two sequential calls.
    /// </summary>
    [Test]
    public async Task A_spend_retried_concurrently_under_one_key_deducts_once()
    {
        await GiveAsync(500, "seed");

        await SpendTogetherAsync(4, 100, _ => "spend-retried");

        var (balance, sum, spends) = await StateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(balance, Is.EqualTo(400));
            Assert.That(sum, Is.EqualTo(balance));
            Assert.That(spends, Is.EqualTo(1));
        });
    }

    /// <summary>FIFO under contention.</summary>
    [Test]
    public async Task Concurrent_spends_still_drain_the_earliest_expiring_lot_first()
    {
        await GiveAsync(100, "seed-early", Now.AddDays(5));
        await GiveAsync(100, "seed-late", Now.AddDays(200));

        // Two hundred in total and three requests for a hundred and fifty, so exactly one can be
        // afforded. The one that wins has to take the whole earlier lot and fifty of the later one.
        await SpendTogetherAsync(3, 150, index => $"spend-{index}");

        await using var db = PostgresTestDatabase.CreateContext();
        var ledger = CreditFixtures.Ledger(db, new TestClock(Now));

        var lots = await ledger.OpenLotsAsync(CreditFixtures.Buyer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(lots, Has.Count.EqualTo(1), "the earlier lot was emptied");
            Assert.That(lots[0].ExpiresAt, Is.EqualTo(Now.AddDays(200)), "and what is left is the later one");
            Assert.That(lots[0].Remaining, Is.EqualTo(50));
        });
    }

    /// <summary>
    /// Concurrent issues, which is the other direction and the one the wallet cap depends on.
    /// </summary>
    [Test]
    public async Task Concurrent_issues_cannot_take_a_wallet_past_its_cap()
    {
        var options = CreditFixtures.Options(walletCap: 1_000);

        using var gate = new SemaphoreSlim(0, 10);

        var runs = Enumerable.Range(0, 10).Select(index => Task.Run(async () =>
        {
            await using var db = PostgresTestDatabase.CreateContext();
            var ledger = CreditFixtures.Ledger(db, new TestClock(Now), options);

            await gate.WaitAsync();

            try
            {
                await ledger.IssueAsync(
                    new IssueCredit(CreditFixtures.Buyer, 300, "Concurrent.", $"issue-{index}"),
                    CancellationToken.None);
            }
            catch (CreditRefusedException)
            {
                // Expected for whichever requests arrive after the cap is reached.
            }
        })).ToArray();

        gate.Release(10);
        await Task.WhenAll(runs);

        var (balance, sum, _) = await StateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(balance, Is.LessThanOrEqualTo(1_000));
            Assert.That(balance, Is.EqualTo(900), "three of 300 fit under a cap of 1,000; a fourth does not");
            Assert.That(sum, Is.EqualTo(balance));
        });
    }

    /// <summary>The database's own backstop, checked directly.</summary>
    [Test]
    public async Task The_database_refuses_a_negative_cached_balance()
    {
        await GiveAsync(100, "seed");

        await using var db = PostgresTestDatabase.CreateContext();

        Assert.That(
            async () => await db.CreditWallets
                .Where(wallet => wallet.UserId == CreditFixtures.Buyer)
                .ExecuteUpdateAsync(update => update.SetProperty(wallet => wallet.CachedBalance, -1)),
            Throws.InstanceOf<DbUpdateException>().Or.InstanceOf<Npgsql.PostgresException>());
    }
}
