namespace Discovery.Domain.Ranking;

/// <summary>The rank inputs for one listing, gathered by the feed query.</summary>
public readonly record struct RankInputs(
    int MatchedTopics,
    int ListingTopics,
    TimeSpan SinceBump,
    int ActiveMembers);

public static class ListingRank
{
    private const double InterestWeight = 0.55;
    private const double FreshnessWeight = 0.25;
    private const double HealthWeight = 0.20;

    private const double HalfLifeDays = 7;

    /// <summary>Caps the health term so one very large guild cannot own the feed.</summary>
    private const int HealthCeiling = 10_000;

    public static double Score(RankInputs inputs) =>
        InterestWeight * Interest(inputs)
        + FreshnessWeight * Freshness(inputs.SinceBump)
        + HealthWeight * Health(inputs.ActiveMembers);

    // Divided by the listing's topic count, not the match count: otherwise a listing that fills all
    // eight topic slots outranks a focused one by breadth alone.
    private static double Interest(RankInputs inputs) =>
        inputs.ListingTopics <= 0
            ? 0
            : Math.Clamp((double)inputs.MatchedTopics / inputs.ListingTopics, 0, 1);

    private static double Freshness(TimeSpan sinceBump) =>
        sinceBump <= TimeSpan.Zero ? 1 : Math.Pow(0.5, sinceBump.TotalDays / HalfLifeDays);

    private static double Health(int activeMembers) =>
        activeMembers <= 0
            ? 0
            : Math.Log(1 + Math.Min(activeMembers, HealthCeiling)) / Math.Log(1 + HealthCeiling);
}
