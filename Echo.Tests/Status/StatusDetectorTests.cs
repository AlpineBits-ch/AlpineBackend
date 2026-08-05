using Echo.Status;

namespace Echo.Tests.Status;

/// <summary>The rules that decide whether the world gets told something is wrong.</summary>
[TestFixture]
[Category("Unit")]
public class StatusDetectorTests
{
    private static readonly StatusOptions Options = new();

    private static StatusThresholds Thresholds => StatusThresholds.For(Options, null, null, null);

    private static StatusReading Window(int total, int errors, int destinations = 1, int unhealthy = 0) =>
        new(new ClusterSample(total, errors), destinations, unhealthy);

    // ── Classification ────────────────────────────────────────────────────────

    [Test]
    public void A_clean_window_is_clean()
    {
        Assert.That(StatusDetector.Classify(Window(1000, 0), Thresholds), Is.EqualTo(StatusVerdict.Clean));
    }

    [Test]
    public void Errors_over_the_degraded_rate_are_degraded()
    {
        // 60 of 1000 is 6%, over the 5% default and under the 25% outage line.
        Assert.That(StatusDetector.Classify(Window(1000, 60), Thresholds), Is.EqualTo(StatusVerdict.Degraded));
    }

    [Test]
    public void Errors_over_the_outage_rate_are_an_outage()
    {
        Assert.That(StatusDetector.Classify(Window(1000, 400), Thresholds), Is.EqualTo(StatusVerdict.Outage));
    }

    /// <summary>The dead band.</summary>
    [Test]
    public void The_band_between_recovery_and_degraded_holds()
    {
        // 3.5%: over the 2% recovery line, under the 5% degraded line.
        Assert.That(StatusDetector.Classify(Window(1000, 35), Thresholds), Is.EqualTo(StatusVerdict.Hold));
    }

    /// <summary>Three failures out of four requests is a 75% error rate and no evidence of
    /// anything.</summary>
    [Test]
    public void Below_the_minimum_volume_a_rate_means_nothing()
    {
        Assert.That(StatusDetector.Classify(Window(4, 3), Thresholds), Is.EqualTo(StatusVerdict.Clean));
    }

    /// <summary>The signal that works at four in the morning: a dead service fails no requests
    /// because nobody is making any.</summary>
    [Test]
    public void Every_destination_unhealthy_is_an_outage_with_no_traffic_at_all()
    {
        var verdict = StatusDetector.Classify(Window(0, 0, destinations: 2, unhealthy: 2), Thresholds);

        Assert.That(verdict, Is.EqualTo(StatusVerdict.Outage));
    }

    /// <summary>"We cannot tell" must never resolve somebody's incident.</summary>
    [Test]
    public void Partial_destination_trouble_with_no_traffic_holds_rather_than_recovering()
    {
        var verdict = StatusDetector.Classify(Window(0, 0, destinations: 3, unhealthy: 1), Thresholds);

        Assert.That(verdict, Is.EqualTo(StatusVerdict.Hold));
    }

    /// <summary>A component tuned for noise uses its own numbers, not the instance defaults.</summary>
    [Test]
    public void A_component_override_replaces_the_instance_threshold()
    {
        var relaxed = StatusThresholds.For(Options, degraded: 0.5, outage: 0.9, minimum: null);

        Assert.That(StatusDetector.Classify(Window(1000, 60), relaxed), Is.EqualTo(StatusVerdict.Hold),
            "6% is over the default degraded rate but far under this component's own");
    }

    // ── Streaks ───────────────────────────────────────────────────────────────

    /// <summary>The regression this whole mechanism exists for: a rolling deploy produces exactly one
    /// ugly window, and it must not become an incident.</summary>
    [Test]
    public void One_bad_window_does_not_open_an_incident()
    {
        var state = new ComponentProbeState();
        state.Apply(StatusVerdict.Degraded);

        Assert.That(StatusDetector.ShouldOpen(StatusVerdict.Degraded, state, Options), Is.False);
    }

    [Test]
    public void Two_consecutive_bad_windows_open_an_incident()
    {
        var state = new ComponentProbeState();
        state.Apply(StatusVerdict.Degraded);
        state.Apply(StatusVerdict.Degraded);

        Assert.That(StatusDetector.ShouldOpen(StatusVerdict.Degraded, state, Options), Is.True);
    }

    [Test]
    public void A_clean_window_between_two_bad_ones_resets_the_streak()
    {
        var state = new ComponentProbeState();
        state.Apply(StatusVerdict.Outage);
        state.Apply(StatusVerdict.Clean);
        state.Apply(StatusVerdict.Outage);

        Assert.That(StatusDetector.ShouldOpen(StatusVerdict.Outage, state, Options), Is.False);
    }

    /// <summary>Holding is not a reset. That is the entire purpose of the dead band.</summary>
    [Test]
    public void A_hold_between_two_bad_windows_preserves_the_streak()
    {
        var state = new ComponentProbeState();
        state.Apply(StatusVerdict.Degraded);
        state.Apply(StatusVerdict.Hold);
        state.Apply(StatusVerdict.Degraded);

        Assert.That(state.BadStreak, Is.EqualTo(2));
        Assert.That(StatusDetector.ShouldOpen(StatusVerdict.Degraded, state, Options), Is.True);
    }

    /// <summary>Quick to say something is wrong, slow to say it is fixed.</summary>
    [Test]
    public void Recovery_needs_more_consecutive_windows_than_opening_did()
    {
        var state = new ComponentProbeState();

        for (var i = 0; i < Options.OpenSamples; i++) state.Apply(StatusVerdict.Clean);

        Assert.That(StatusDetector.ShouldRecover(StatusVerdict.Clean, state, Options), Is.False,
            "as many clean windows as it took to open must not be enough to close");

        for (var i = state.CleanStreak; i < Options.RecoverySamples; i++) state.Apply(StatusVerdict.Clean);

        Assert.That(StatusDetector.ShouldRecover(StatusVerdict.Clean, state, Options), Is.True);
    }

    [Test]
    public void A_bad_window_during_recovery_stops_it()
    {
        var state = new ComponentProbeState();

        for (var i = 0; i < Options.RecoverySamples - 1; i++) state.Apply(StatusVerdict.Clean);
        state.Apply(StatusVerdict.Degraded);
        state.Apply(StatusVerdict.Clean);

        Assert.That(StatusDetector.ShouldRecover(StatusVerdict.Clean, state, Options), Is.False);
    }

    // ── Retraction ────────────────────────────────────────────────────────────

    [Test]
    public void An_incident_shorter_than_the_threshold_is_retracted()
    {
        Assert.That(StatusDetector.ShouldRetract(TimeSpan.FromSeconds(30), Options), Is.True);
    }

    [Test]
    public void An_incident_longer_than_the_threshold_is_published()
    {
        Assert.That(StatusDetector.ShouldRetract(TimeSpan.FromMinutes(10), Options), Is.False);
    }

    // ── Options parsing ───────────────────────────────────────────────────────

    /// <summary>Somebody will type <c>5</c> meaning five percent.</summary>
    [Test]
    public void A_rate_given_as_a_percentage_is_read_as_one()
    {
        Environment.SetEnvironmentVariable("STATUS_DEGRADED_RATE", "5");

        try
        {
            Assert.That(StatusOptions.FromEnvironment().DegradedRate, Is.EqualTo(0.05).Within(0.0001));
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATUS_DEGRADED_RATE", null);
        }
    }

    [Test]
    public void A_nonsense_rate_falls_back_to_the_default()
    {
        Environment.SetEnvironmentVariable("STATUS_OUTAGE_RATE", "not a number");

        try
        {
            Assert.That(StatusOptions.FromEnvironment().OutageRate, Is.EqualTo(new StatusOptions().OutageRate));
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATUS_OUTAGE_RATE", null);
        }
    }
}
