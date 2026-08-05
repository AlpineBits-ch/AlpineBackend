namespace Echo.Status;

/// <summary>One evaluation's opinion about one component.</summary>
public enum StatusVerdict
{
    /// <summary>Under the recovery rate. Counts toward closing an incident.</summary>
    Clean,

    /// <summary>In the dead band, or too little traffic to say. Changes nothing.</summary>
    Hold,

    Degraded,
    Outage,
}

/// <summary>What one window looked like: the counted responses, and how many backends were up.</summary>
public readonly record struct StatusReading(ClusterSample Sample, int Destinations, int Unhealthy);

/// <summary>The thresholds an evaluation is judged against, after per-component overrides.</summary>
public readonly record struct StatusThresholds(
    double DegradedRate, double OutageRate, double RecoveryRate, int MinimumVolume)
{
    public static StatusThresholds For(StatusOptions options, double? degraded, double? outage, int? minimum) =>
        new(degraded ?? options.DegradedRate,
            outage ?? options.OutageRate,
            options.RecoveryRate,
            minimum ?? options.MinimumVolume);
}

/// <summary>
/// The decision rules, lifted out of the probe so they can be exercised without a host, a clock or
/// a database.
/// </summary>
public static class StatusDetector
{
    /// <summary>Turns one window into one of four opinions.</summary>
    public static StatusVerdict Classify(StatusReading reading, StatusThresholds thresholds)
    {
        // Every destination down is an outage whatever the traffic says, and it is the only signal
        // that works at four in the morning when a dead service is failing no requests because
        // nobody is making any.
        if (reading.Destinations > 0 && reading.Unhealthy == reading.Destinations) return StatusVerdict.Outage;

        if (reading.Sample.Total < thresholds.MinimumVolume)
        {
            // Too little traffic to believe a rate.
            return reading.Unhealthy > 0 ? StatusVerdict.Hold : StatusVerdict.Clean;
        }

        var rate = reading.Sample.ErrorRate;

        if (rate >= thresholds.OutageRate) return StatusVerdict.Outage;
        if (rate >= thresholds.DegradedRate) return StatusVerdict.Degraded;

        return rate < thresholds.RecoveryRate ? StatusVerdict.Clean : StatusVerdict.Hold;
    }

    public static bool IsBad(StatusVerdict verdict) =>
        verdict is StatusVerdict.Degraded or StatusVerdict.Outage;

    /// <summary>Two consecutive bad windows, not one: a rolling deploy produces exactly one ugly
    /// window, and a status page that flaps is worse than one that is thirty seconds late.</summary>
    public static bool ShouldOpen(StatusVerdict verdict, ComponentProbeState state, StatusOptions options) =>
        IsBad(verdict) && state.BadStreak >= options.OpenSamples;

    /// <summary>Deliberately slower than opening.</summary>
    public static bool ShouldRecover(StatusVerdict verdict, ComponentProbeState state, StatusOptions options) =>
        verdict == StatusVerdict.Clean && state.CleanStreak >= options.RecoverySamples;

    /// <summary>An automatic incident nobody touched, over before anyone could have read it. It is
    /// hidden rather than published, and kept rather than deleted.</summary>
    public static bool ShouldRetract(TimeSpan duration, StatusOptions options) =>
        duration < options.RetractionThreshold;
}

/// <summary>Consecutive-evaluation counters for one component.</summary>
public sealed class ComponentProbeState
{
    public int BadStreak { get; private set; }
    public int CleanStreak { get; private set; }

    public void Apply(StatusVerdict verdict)
    {
        switch (verdict)
        {
            case StatusVerdict.Clean:
                CleanStreak++;
                BadStreak = 0;
                break;

            case StatusVerdict.Degraded:
            case StatusVerdict.Outage:
                BadStreak++;
                CleanStreak = 0;
                break;

            case StatusVerdict.Hold:
                // Neither counter moves.
                break;
        }
    }
}
