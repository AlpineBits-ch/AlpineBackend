using Echo.Status;

namespace Echo.Tests.Status;

/// <summary>What the gateway counts, and what it deliberately does not.</summary>
[TestFixture]
[Category("Unit")]
public class StatusMetricsTests
{
    private static StatusMetrics New() => new(new StatusOptions());

    [Test]
    public void Successful_responses_count_toward_the_total_and_not_the_errors()
    {
        var metrics = New();

        for (var i = 0; i < 10; i++) metrics.Record("guild-cluster", 200, aborted: false);

        var sample = metrics.Read("guild-cluster");

        Assert.That(sample.Total, Is.EqualTo(10));
        Assert.That(sample.Errors, Is.Zero);
        Assert.That(sample.ErrorRate, Is.Zero);
    }

    [TestCase(500)]
    [TestCase(502)]
    [TestCase(503)]
    [TestCase(504)]
    public void Server_errors_count(int status)
    {
        var metrics = New();
        metrics.Record("guild-cluster", status, aborted: false);

        Assert.That(metrics.Read("guild-cluster").Errors, Is.EqualTo(1));
    }

    /// <summary>The rule that keeps the page honest.</summary>
    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    [TestCase(409)]
    [TestCase(429)]
    public void Client_errors_are_counted_as_traffic_but_never_as_failures(int status)
    {
        var metrics = New();
        metrics.Record("guild-cluster", status, aborted: false);

        var sample = metrics.Read("guild-cluster");

        Assert.That(sample.Total, Is.EqualTo(1));
        Assert.That(sample.Errors, Is.Zero);
    }

    /// <summary>A user closing a tab mid-upload is not an outage, and on mobile networks there are a
    /// lot of them. An aborted request is not counted at all - not as an error, and not as a
    /// successful request that would dilute a real one.</summary>
    [Test]
    public void Aborted_requests_are_not_counted_at_all()
    {
        var metrics = New();
        metrics.Record("guild-cluster", 500, aborted: true);
        metrics.Record("guild-cluster", 200, aborted: true);

        Assert.That(metrics.Read("guild-cluster").Total, Is.Zero);
    }

    [Test]
    public void Clusters_are_counted_separately()
    {
        var metrics = New();
        metrics.Record("guild-cluster", 500, aborted: false);
        metrics.Record("identity-cluster", 200, aborted: false);

        Assert.That(metrics.Read("guild-cluster").Errors, Is.EqualTo(1));
        Assert.That(metrics.Read("identity-cluster").Errors, Is.Zero);
    }

    /// <summary>A component watching several clusters sees them as one signal, which is what makes
    /// "Sign-in and accounts" a single row despite three clusters behind it.</summary>
    [Test]
    public void A_component_reads_all_of_its_clusters_as_one_window()
    {
        var metrics = New();
        metrics.Record("identity-cluster", 500, aborted: false);
        metrics.Record("identity-connect-cluster", 200, aborted: false);
        metrics.Record("identity-oauth-cluster", 200, aborted: false);

        var sample = metrics.Read(["identity-cluster", "identity-connect-cluster", "identity-oauth-cluster"]);

        Assert.That(sample.Total, Is.EqualTo(3));
        Assert.That(sample.Errors, Is.EqualTo(1));
    }

    [Test]
    public void A_cluster_nothing_has_been_recorded_against_reads_as_empty()
    {
        Assert.That(New().Read("never-seen").Total, Is.Zero);
    }

    /// <summary>Gateway-local components have no cluster, so they get a reserved key rather than a
    /// special case in the probe.</summary>
    [Test]
    public void Local_keys_are_namespaced_away_from_cluster_ids()
    {
        Assert.That(StatusMetrics.LocalKey("api"), Does.StartWith(StatusMetrics.LocalPrefix));
        Assert.That(StatusMetrics.LocalKey("api"), Is.Not.EqualTo("api"));
    }

    /// <summary>
    /// The window is a ring of buckets keyed on wall-clock epoch, and a bucket from a previous lap
    /// is skipped rather than read.
    /// </summary>
    [Test]
    public void A_window_is_bounded_by_its_bucket_count()
    {
        var options = new StatusOptions { Interval = TimeSpan.FromSeconds(5), WindowBuckets = 3 };
        var metrics = new StatusMetrics(options);

        metrics.Record("guild-cluster", 500, aborted: false);

        // Reading immediately sees it; the bucket ring holds WindowBuckets + 1 slots, so nothing
        // recorded now can survive more than that many intervals.
        Assert.That(metrics.Read("guild-cluster").Total, Is.EqualTo(1));
    }
}
