using System.Globalization;

namespace Echo.Status;

/// <summary>
/// What the detector believes, and how sure it has to be before it says anything in public.
/// </summary>
public sealed class StatusOptions
{
    /// <summary>How often the probe evaluates. Also the bucket width, so these cannot drift apart.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Number of buckets summed into one evaluation.</summary>
    public int WindowBuckets { get; init; } = 3;

    /// <summary>Requests needed in the window before an error rate means anything.</summary>
    public int MinimumVolume { get; init; } = 20;

    public double DegradedRate { get; init; } = 0.05;
    public double OutageRate { get; init; } = 0.25;

    /// <summary>
    /// The rate a component has to get back under to count as clean, deliberately lower than <see
    /// cref="DegradedRate"/>.
    /// </summary>
    public double RecoveryRate { get; init; } = 0.02;

    /// <summary>Consecutive bad evaluations before an incident opens.</summary>
    public int OpenSamples { get; init; } = 2;

    /// <summary>Consecutive clean evaluations before an automatic incident resolves.</summary>
    public int RecoverySamples { get; init; } = 5;

    /// <summary>An automatic incident shorter than this, which nobody touched, is retracted rather
    /// than published. Publishing blips trains people to ignore the page.</summary>
    public TimeSpan RetractionThreshold { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Within this long of an automatic incident resolving, the same component reopens it
    /// instead of opening another. A service that dies every four minutes should be one incident
    /// with a messy timeline, not fifteen incidents.</summary>
    public TimeSpan ReopenWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the public summary may be served from the last build.</summary>
    public TimeSpan PublicCacheDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Master switch.</summary>
    public bool AutoDetectionEnabled { get; init; } = true;

    public static StatusOptions FromEnvironment() => new()
    {
        Interval = Seconds("STATUS_PROBE_INTERVAL_SECONDS", 20),
        MinimumVolume = Integer("STATUS_MINIMUM_VOLUME", 20),
        DegradedRate = Rate("STATUS_DEGRADED_RATE", 0.05),
        OutageRate = Rate("STATUS_OUTAGE_RATE", 0.25),
        RecoveryRate = Rate("STATUS_RECOVERY_RATE", 0.02),
        OpenSamples = Integer("STATUS_OPEN_SAMPLES", 2),
        RecoverySamples = Integer("STATUS_RECOVERY_SAMPLES", 5),
        RetractionThreshold = Seconds("STATUS_RETRACTION_SECONDS", 120),
        AutoDetectionEnabled = Boolean("STATUS_AUTO_DETECTION", true),
    };

    private static TimeSpan Seconds(string variable, int fallback) =>
        TimeSpan.FromSeconds(Math.Clamp(Integer(variable, fallback), 5, 3600));

    private static int Integer(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    /// <summary>Accepts both <c>0.05</c> and <c>5</c> for five percent.</summary>
    private static double Rate(string variable, double fallback)
    {
        if (!double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return fallback;
        }

        return value > 1 ? Math.Min(value / 100d, 1d) : value;
    }

    private static bool Boolean(string variable, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim() is not ("0" or "false" or "False" or "no");
    }
}
