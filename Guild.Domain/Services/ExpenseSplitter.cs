using Guild.Domain.Enums;

namespace Guild.Domain.Services;

public record SplitParticipant(string UserId, decimal ShareValue);

public record SplitResult(string UserId, long AmountMinor);

/// <summary>Divides an expense total across participants in integer minor units.</summary>
public static class ExpenseSplitter
{
    public static IReadOnlyList<SplitResult> Split(
        long totalMinor,
        ExpenseSplitKind kind,
        IReadOnlyList<SplitParticipant> participants)
    {
        if (participants.Count == 0)
            throw new ArgumentException("An expense needs at least one participant", nameof(participants));

        return kind switch
        {
            ExpenseSplitKind.Equal => SplitEqual(totalMinor, participants),
            ExpenseSplitKind.Shares => SplitByShares(totalMinor, participants),
            ExpenseSplitKind.Exact => SplitExact(totalMinor, participants),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static IReadOnlyList<SplitResult> SplitEqual(
        long totalMinor, IReadOnlyList<SplitParticipant> participants)
    {
        var ordered = participants.OrderBy(p => p.UserId, StringComparer.Ordinal).ToList();
        var count = ordered.Count;

        // Integer division truncates toward zero for positives; the remainder is handed out one
        // unit at a time below. 1000 / 3 -> 334, 333, 333.
        var baseAmount = totalMinor / count;
        var remainder = totalMinor - baseAmount * count;

        return ordered
            .Select((p, i) => new SplitResult(p.UserId, baseAmount + (i < Math.Abs(remainder) ? Math.Sign(remainder) : 0)))
            .ToList();
    }

    private static IReadOnlyList<SplitResult> SplitByShares(
        long totalMinor, IReadOnlyList<SplitParticipant> participants)
    {
        var ordered = participants.OrderBy(p => p.UserId, StringComparer.Ordinal).ToList();
        var totalShares = ordered.Sum(p => p.ShareValue);

        if (totalShares <= 0)
            throw new ArgumentException("Share weights must sum to more than zero", nameof(participants));

        // Largest-remainder apportionment: floor everything, then give the leftover units to
        // whoever was rounded down hardest.
        var floored = ordered
            .Select(p =>
            {
                var exact = (decimal)totalMinor * p.ShareValue / totalShares;
                var whole = (long)Math.Floor(exact);
                return (p.UserId, Whole: whole, Fraction: exact - whole);
            })
            .ToList();

        var distributed = floored.Sum(f => f.Whole);
        var remainder = totalMinor - distributed;

        var order = floored
            .OrderByDescending(f => f.Fraction)
            .ThenBy(f => f.UserId, StringComparer.Ordinal)
            .Select(f => f.UserId)
            .ToList();

        var bonus = order.Take((int)Math.Abs(remainder)).ToHashSet(StringComparer.Ordinal);
        var step = Math.Sign(remainder);

        return floored
            .Select(f => new SplitResult(f.UserId, f.Whole + (bonus.Contains(f.UserId) ? step : 0)))
            .ToList();
    }

    private static IReadOnlyList<SplitResult> SplitExact(
        long totalMinor, IReadOnlyList<SplitParticipant> participants)
    {
        var amounts = participants
            .Select(p => new SplitResult(p.UserId, (long)p.ShareValue))
            .ToList();

        var sum = amounts.Sum(a => a.AmountMinor);
        if (sum != totalMinor)
            throw new ArgumentException(
                $"Exact split amounts sum to {sum} but the expense total is {totalMinor}");

        return amounts;
    }
}

public record BalanceEntry(string UserId, long NetMinor);

public record TransferSuggestion(string FromUserId, string ToUserId, long AmountMinor);

/// <summary>Turns a set of net balances into the smallest practical set of payments.</summary>
public static class DebtSimplifier
{
    /// <summary>Greedy largest-creditor / largest-debtor matching.</summary>
    public static IReadOnlyList<TransferSuggestion> Simplify(IReadOnlyList<BalanceEntry> balances)
    {
        // Ordinal UserId tiebreaks keep the suggestion stable between calls, so a client polling
        // this doesn't see the list reshuffle underneath the user.
        var creditors = balances.Where(b => b.NetMinor > 0)
            .OrderByDescending(b => b.NetMinor).ThenBy(b => b.UserId, StringComparer.Ordinal)
            .Select(b => (b.UserId, Amount: b.NetMinor)).ToList();
        var debtors = balances.Where(b => b.NetMinor < 0)
            .OrderBy(b => b.NetMinor).ThenBy(b => b.UserId, StringComparer.Ordinal)
            .Select(b => (b.UserId, Amount: -b.NetMinor)).ToList();

        var transfers = new List<TransferSuggestion>();
        int ci = 0, di = 0;

        while (ci < creditors.Count && di < debtors.Count)
        {
            var take = Math.Min(creditors[ci].Amount, debtors[di].Amount);
            if (take > 0)
                transfers.Add(new TransferSuggestion(debtors[di].UserId, creditors[ci].UserId, take));

            creditors[ci] = (creditors[ci].UserId, creditors[ci].Amount - take);
            debtors[di] = (debtors[di].UserId, debtors[di].Amount - take);

            if (creditors[ci].Amount == 0) ci++;
            if (debtors[di].Amount == 0) di++;
        }

        return transfers;
    }
}
