namespace Echo.Entitlements.Keys;

/// <summary>The ordered rungs of a ladder-valued key, lowest first.</summary>
public sealed class EntitlementLadder
{
    public EntitlementLadder(string name, params string[] rungs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rungs);

        if (rungs.Length < 2)
        {
            throw new ArgumentException(
                $"Ladder '{name}' needs at least two rungs. A one-rung ladder is a flag with extra steps.",
                nameof(rungs));
        }

        if (rungs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rungs.Length)
        {
            throw new ArgumentException(
                $"Ladder '{name}' has duplicate rungs, so a rung name would not identify a single rank.",
                nameof(rungs));
        }

        Name = name;
        Rungs = rungs;
    }

    public string Name { get; }

    /// <summary>Ascending. Index is the rank.</summary>
    public IReadOnlyList<string> Rungs { get; }

    public int LowestRank => 0;

    public int HighestRank => Rungs.Count - 1;

    public bool TryRankOf(string rung, out int rank)
    {
        for (var i = 0; i < Rungs.Count; i++)
        {
            if (string.Equals(Rungs[i], rung, StringComparison.OrdinalIgnoreCase))
            {
                rank = i;
                return true;
            }
        }

        rank = -1;
        return false;
    }

    public int RankOf(string rung) =>
        TryRankOf(rung, out var rank)
            ? rank
            : throw new ArgumentException(
                $"'{rung}' is not a rung of ladder '{Name}'. Known rungs, lowest first: {string.Join(", ", Rungs)}.",
                nameof(rung));

    public string RungAt(int rank) =>
        rank >= 0 && rank < Rungs.Count
            ? Rungs[rank]
            : throw new ArgumentOutOfRangeException(nameof(rank), rank,
                $"Ladder '{Name}' has ranks 0..{HighestRank}.");

    /// <summary>The rung name for a value of this ladder's key, for display and for the admin
    /// provenance screen.</summary>
    public string Describe(Model.EntitlementValue value) => RungAt(value.AsRank);
}
