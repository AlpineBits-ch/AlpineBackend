using Guild.Domain.Enums;
using Guild.Domain.Services;

namespace Guild.Tests.Domain;

/// <summary>The money maths.
///
/// One invariant matters above everything else here: <b>shares sum to exactly the expense total,
/// and net balances sum to exactly zero</b>. A ledger that drifts by a rappen is a ledger nobody
/// can settle, and it's the kind of bug that shows up in someone's actual bank account rather
/// than in a stack trace. Hence the exhaustive sweeps rather than a couple of spot checks.</summary>
[TestFixture]
public class HouseholdMoneyTests
{
    private static List<SplitParticipant> Equal(params string[] userIds) =>
        userIds.Select(id => new SplitParticipant(id, 1)).ToList();

    // ══════════════════════════════════════════════════════════════════════════
    // Equal splits
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void EqualSplit_IndivisibleAmount_DistributesRemainder()
    {
        var result = ExpenseSplitter.Split(1000, ExpenseSplitKind.Equal, Equal("a", "b", "c"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Sum(r => r.AmountMinor), Is.EqualTo(1000), "nothing may be lost to rounding");
            Assert.That(result.Select(r => r.AmountMinor).OrderByDescending(x => x),
                Is.EqualTo(new long[] { 334, 333, 333 }));
        });
    }

    [Test]
    public void EqualSplit_RemainderGoesToLowestUserIdsDeterministically()
    {
        // Input order deliberately reversed - the outcome must not depend on it, or two clients
        // sending the same expense differently ordered would produce different ledgers.
        var forwards = ExpenseSplitter.Split(100, ExpenseSplitKind.Equal, Equal("a", "b", "c"));
        var backwards = ExpenseSplitter.Split(100, ExpenseSplitKind.Equal, Equal("c", "b", "a"));

        Assert.That(forwards.OrderBy(r => r.UserId).Select(r => r.AmountMinor),
            Is.EqualTo(backwards.OrderBy(r => r.UserId).Select(r => r.AmountMinor)));
    }

    [Test]
    public void EqualSplit_SumsToTotal_ForEveryAmountAndPartySize()
    {
        // The exhaustive version of the property above: every remainder case for realistic
        // household sizes.
        for (var people = 1; people <= 8; people++)
        {
            var participants = Equal(Enumerable.Range(0, people).Select(i => $"user-{i}").ToArray());

            for (long total = 1; total <= 200; total++)
            {
                var result = ExpenseSplitter.Split(total, ExpenseSplitKind.Equal, participants);
                Assert.That(result.Sum(r => r.AmountMinor), Is.EqualTo(total),
                    $"equal split of {total} across {people}");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Share-weighted splits
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void SharesSplit_WeightsProportionally()
    {
        var result = ExpenseSplitter.Split(300, ExpenseSplitKind.Shares,
        [
            new SplitParticipant("a", 2),
            new SplitParticipant("b", 1),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.First(r => r.UserId == "a").AmountMinor, Is.EqualTo(200));
            Assert.That(result.First(r => r.UserId == "b").AmountMinor, Is.EqualTo(100));
            Assert.That(result.Sum(r => r.AmountMinor), Is.EqualTo(300));
        });
    }

    [Test]
    public void SharesSplit_EqualWeights_MatchesEqualSplit()
    {
        // Largest-remainder apportionment has to degrade to exactly the equal-split result when
        // every weight is the same, or the two code paths would disagree on the same expense.
        var shares = ExpenseSplitter.Split(1000, ExpenseSplitKind.Shares, Equal("a", "b", "c"));
        var equal = ExpenseSplitter.Split(1000, ExpenseSplitKind.Equal, Equal("a", "b", "c"));

        Assert.That(shares.OrderBy(r => r.UserId).Select(r => r.AmountMinor),
            Is.EqualTo(equal.OrderBy(r => r.UserId).Select(r => r.AmountMinor)));
    }

    [Test]
    public void SharesSplit_SumsToTotal_AcrossAwkwardWeights()
    {
        var participants = new List<SplitParticipant>
        {
            new("a", 1), new("b", 3), new("c", 7), new("d", 2),
        };

        for (long total = 1; total <= 500; total++)
        {
            var result = ExpenseSplitter.Split(total, ExpenseSplitKind.Shares, participants);
            Assert.That(result.Sum(r => r.AmountMinor), Is.EqualTo(total), $"shares split of {total}");
        }
    }

    [Test]
    public void SharesSplit_ZeroTotalWeight_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ExpenseSplitter.Split(100, ExpenseSplitKind.Shares, [new SplitParticipant("a", 0)]));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Exact splits
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ExactSplit_UsesSuppliedAmounts()
    {
        var result = ExpenseSplitter.Split(1000, ExpenseSplitKind.Exact,
        [
            new SplitParticipant("a", 700),
            new SplitParticipant("b", 300),
        ]);

        Assert.That(result.Sum(r => r.AmountMinor), Is.EqualTo(1000));
    }

    [Test]
    public void ExactSplit_ThatDoesNotSumToTotal_IsRejected()
    {
        // The one split kind where the client supplies the numbers, so the one that has to be
        // checked rather than computed.
        var exception = Assert.Throws<ArgumentException>(() =>
            ExpenseSplitter.Split(1000, ExpenseSplitKind.Exact,
            [
                new SplitParticipant("a", 700),
                new SplitParticipant("b", 200),
            ]));

        Assert.That(exception!.Message, Does.Contain("900").And.Contain("1000"));
    }

    [Test]
    public void Split_WithNoParticipants_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ExpenseSplitter.Split(100, ExpenseSplitKind.Equal, []));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Debt simplification
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Simplify_ClearsEveryBalance()
    {
        var balances = new List<BalanceEntry>
        {
            new("anna", 5000), new("ben", -2000), new("cara", -3000),
        };

        var transfers = DebtSimplifier.Simplify(balances);

        var net = balances.ToDictionary(b => b.UserId, b => b.NetMinor);
        foreach (var transfer in transfers)
        {
            net[transfer.FromUserId] += transfer.AmountMinor;
            net[transfer.ToUserId] -= transfer.AmountMinor;
        }

        Assert.Multiple(() =>
        {
            Assert.That(net.Values, Is.All.EqualTo(0), "every balance must land on zero");
            Assert.That(transfers, Has.Count.LessThanOrEqualTo(balances.Count - 1),
                "at most n-1 transfers - the point is fewer payments, not a payment per pair");
            Assert.That(transfers.Select(t => t.AmountMinor), Is.All.GreaterThan(0));
        });
    }

    [Test]
    public void Simplify_FourWayTangle_StaysUnderTransferBound()
    {
        var balances = new List<BalanceEntry>
        {
            new("a", 1500), new("b", 700), new("c", -900), new("d", -1300),
        };

        var transfers = DebtSimplifier.Simplify(balances);

        var net = balances.ToDictionary(b => b.UserId, b => b.NetMinor);
        foreach (var transfer in transfers)
        {
            net[transfer.FromUserId] += transfer.AmountMinor;
            net[transfer.ToUserId] -= transfer.AmountMinor;
        }

        Assert.Multiple(() =>
        {
            Assert.That(net.Values, Is.All.EqualTo(0));
            Assert.That(transfers, Has.Count.LessThanOrEqualTo(3));
        });
    }

    [Test]
    public void Simplify_SettledHouse_SuggestsNothing()
    {
        Assert.That(DebtSimplifier.Simplify([]), Is.Empty);
    }

    /// <summary>The end-to-end invariant: expenses in, balances out, sum zero. This is the
    /// property the whole ledger rests on.</summary>
    [Test]
    public void Balances_FromSplits_AlwaysSumToZero()
    {
        var members = new[] { "anna", "ben", "cara" };
        var net = members.ToDictionary(m => m, _ => 0L);

        void RecordExpense(string payer, long amount, ExpenseSplitKind kind, List<SplitParticipant> participants)
        {
            var split = ExpenseSplitter.Split(amount, kind, participants);
            net[payer] += amount;
            foreach (var share in split) net[share.UserId] -= share.AmountMinor;
        }

        RecordExpense("anna", 1000, ExpenseSplitKind.Equal, Equal(members));
        RecordExpense("ben", 777, ExpenseSplitKind.Equal, Equal(members));
        RecordExpense("cara", 5000, ExpenseSplitKind.Shares,
            [new SplitParticipant("anna", 2), new SplitParticipant("ben", 1), new SplitParticipant("cara", 1)]);
        RecordExpense("anna", 333, ExpenseSplitKind.Exact,
            [new SplitParticipant("ben", 111), new SplitParticipant("cara", 222)]);

        Assert.That(net.Values.Sum(), Is.EqualTo(0));
    }
}
