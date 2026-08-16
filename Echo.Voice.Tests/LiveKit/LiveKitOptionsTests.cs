using Echo.Realtime.LiveKit;

namespace Echo.Voice.Tests.LiveKit;

/// <summary>Reading the fleet out of the environment.</summary>
[TestFixture]
[NonParallelizable]
public class LiveKitOptionsTests
{
    private static readonly string[] Variables =
    [
        "LIVEKIT_API_KEY", "LIVEKIT_API_SECRET",
        "LIVEKIT__NODES__0__REGION", "LIVEKIT__NODES__0__SIGNALINGURL", "LIVEKIT__NODES__0__APIURL",
        "LIVEKIT__NODES__1__REGION", "LIVEKIT__NODES__1__SIGNALINGURL", "LIVEKIT__NODES__1__APIURL",
        "LIVEKIT__NODES__2__REGION", "LIVEKIT__NODES__2__SIGNALINGURL", "LIVEKIT__NODES__2__APIURL",
    ];

    [SetUp]
    [TearDown]
    public void Clear()
    {
        foreach (var name in Variables) Environment.SetEnvironmentVariable(name, null);
    }

    private static void Node(int index, string region, string signaling, string api)
    {
        Environment.SetEnvironmentVariable($"LIVEKIT__NODES__{index}__REGION", region);
        Environment.SetEnvironmentVariable($"LIVEKIT__NODES__{index}__SIGNALINGURL", signaling);
        Environment.SetEnvironmentVariable($"LIVEKIT__NODES__{index}__APIURL", api);
    }

    private static void Credentials()
    {
        Environment.SetEnvironmentVariable("LIVEKIT_API_KEY", "APIsomething");
        Environment.SetEnvironmentVariable("LIVEKIT_API_SECRET", "a-secret");
    }

    [Test]
    public void A_single_node_is_read_from_the_indexed_variables()
    {
        Credentials();
        Node(0, "fsn1", "wss://sfu-fsn1.venta.gg", "http://10.10.0.2:7880");

        var options = LiveKitOptions.FromEnvironment();

        Assert.Multiple(() =>
        {
            Assert.That(options.IsConfigured, Is.True);
            Assert.That(options.Nodes, Has.Count.EqualTo(1));
            Assert.That(options.Nodes[0].Region, Is.EqualTo("fsn1"));
            Assert.That(options.Nodes[0].SignalingUrl, Is.EqualTo("wss://sfu-fsn1.venta.gg"));
            Assert.That(options.Nodes[0].ApiUrl, Is.EqualTo("http://10.10.0.2:7880"));
        });
    }

    [Test]
    public void Several_nodes_are_read_in_order()
    {
        Credentials();
        Node(0, "fsn1", "wss://a", "http://10.10.0.2:7880");
        Node(1, "ash", "wss://b", "http://10.10.0.3:7880");

        var options = LiveKitOptions.FromEnvironment();

        Assert.Multiple(() =>
        {
            Assert.That(options.Nodes.Select(n => n.Region), Is.EqualTo(new[] { "fsn1", "ash" }));
            Assert.That(options.Node("ash")!.SignalingUrl, Is.EqualTo("wss://b"));
            Assert.That(options.SoleNode, Is.Null, "there is no defensible default with two nodes");
        });
    }

    /// <summary>A gap ends the list, so a typo in an index is a node that silently does not exist
    /// rather than one that half does - which is the failure that would place rooms on a node nobody
    /// can be routed to.</summary>
    [Test]
    public void A_gap_in_the_indices_ends_the_list()
    {
        Credentials();
        Node(0, "fsn1", "wss://a", "http://10.10.0.2:7880");
        Node(2, "ash", "wss://b", "http://10.10.0.3:7880");

        Assert.That(LiveKitOptions.FromEnvironment().Nodes, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_node_missing_a_url_is_skipped_and_the_rest_survive()
    {
        Credentials();
        Environment.SetEnvironmentVariable("LIVEKIT__NODES__0__REGION", "broken");
        Node(1, "ash", "wss://b", "http://10.10.0.3:7880");

        var options = LiveKitOptions.FromEnvironment();

        Assert.Multiple(() =>
        {
            Assert.That(options.Nodes.Select(n => n.Region), Is.EqualTo(new[] { "ash" }),
                "there is nothing useful to do with a node that can be signalled but not controlled");
            Assert.That(options.Node("broken"), Is.Null);
        });
    }

    // ── When voice is simply not set up ───────────────────────────────────────

    [Test]
    public void Nothing_configured_reads_as_not_configured_rather_than_half_configured()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LiveKitOptions.FromEnvironment().IsConfigured, Is.False);
            Assert.That(LiveKitOptions.FromEnvironment().Nodes, Is.Empty);
        });
    }

    [Test]
    public void Credentials_without_a_node_are_not_configured()
    {
        Credentials();

        Assert.That(LiveKitOptions.FromEnvironment().IsConfigured, Is.False,
            "a key with nowhere to point it mints tokens for rooms that cannot exist");
    }

    /// <summary>
    /// The key and secret were committed as placeholders, and the deployment notes single this out
    /// as the first thing to check before debugging anything else.
    /// </summary>
    [Test]
    public void The_committed_placeholder_does_not_read_as_configured()
    {
        Environment.SetEnvironmentVariable("LIVEKIT_API_KEY", "REPLACE_ME");
        Environment.SetEnvironmentVariable("LIVEKIT_API_SECRET", "REPLACE_ME");
        Node(0, "fsn1", "wss://a", "http://10.10.0.2:7880");

        Assert.That(LiveKitOptions.FromEnvironment().IsConfigured, Is.False);
    }

    [Test]
    public void A_trailing_slash_on_either_url_is_trimmed()
    {
        Credentials();
        Node(0, "fsn1", "wss://sfu-fsn1.venta.gg/", "http://10.10.0.2:7880/");

        var node = LiveKitOptions.FromEnvironment().Nodes.Single();

        Assert.Multiple(() =>
        {
            // The control URL is concatenated with "/twirp/..." at every call site, so a trailing
            // slash here is a double slash in every path - which some proxies answer 404 to.
            Assert.That(node.ApiUrl, Is.EqualTo("http://10.10.0.2:7880"));
            Assert.That(node.SignalingUrl, Is.EqualTo("wss://sfu-fsn1.venta.gg"));
        });
    }
}
