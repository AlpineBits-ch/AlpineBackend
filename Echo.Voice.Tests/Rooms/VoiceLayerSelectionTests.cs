using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tests.Usage;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// What the plan says about one subscribe request, which is where the two halves of the cost work
/// stop being advice: the refusal of a pull the subscriber is not supposed to be making, and the
/// simulcast layer that actually reaches the SFU.
/// </summary>
[TestFixture]
[NonParallelizable]
public class VoiceLayerSelectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly VoiceRoomKey _key = VoiceRoomKey.Channel("channel-1");

    private VoiceTestHarness.SerializingLockService _locks = null!;
    private VoiceTestHarness _h = null!;
    private FakeClock _clock = null!;

    /// <summary>Three, so a five-person room is a large one. The production default is ten.</summary>
    private static readonly VoiceSubscriptionOptions Options = new()
    {
        ActiveSpeakerThreshold = 3,
        ActiveSpeakerCount = 2,
        MaxActiveSpeakers = 3,
    };

    [SetUp]
    public void SetUp()
    {
        _locks = new VoiceTestHarness.SerializingLockService();
        _h = new VoiceTestHarness(_locks);
        _clock = new FakeClock(Start);
    }

    private VoiceRoomService ServiceFor(VoiceSubscriptionOptions options) =>
        new(_h.Rooms, _h.Announcer, new VoiceSubscriptions(
            new VoiceAttentionStore(_locks, _h.Cache, options), options, _clock,
            NullLogger<VoiceSubscriptions>.Instance));

    private static async Task PopulateAsync(VoiceRoomService service, VoiceRoomKey key, int size)
    {
        for (var i = 0; i < size; i++)
        {
            var userId = $"u{i:D2}";
            await service.JoinAsync(key, userId, $"device-{i}", "guild-1");
            await service.RecordPublishAsync(key, userId, $"cf-{userId}");
        }
    }

    private static VoiceTrackRef Camera(string publisher, string? layer = null) =>
        new(VoiceTrackDirection.Subscribe, TrackName: "camera",
            MediaSessionId: $"cf-{publisher}", Layer: layer);

    private static string? LayerOf(VoiceSubscribeDecision decision, string trackName) =>
        decision.Tracks.Single(t => t.TrackName == trackName).Layer;

    // ── The names themselves ──────────────────────────────────────────────────

    /// <summary>
    /// Cloudflare's only rid-ordering vocabulary is <c>asciibetical</c>: "a is most desirable, z is
    /// least" under congestion, and "the next available layer after sorting a-z" when a rid stops
    /// being published.
    /// </summary>
    [Test]
    public void Layer_names_sort_alphabetically_in_descending_quality()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                string.CompareOrdinal(VoiceVideoLayers.High, VoiceVideoLayers.Medium), Is.Negative,
                "the SFU sorts rids a-z and reads that order as best-to-worst");
            Assert.That(
                string.CompareOrdinal(VoiceVideoLayers.Medium, VoiceVideoLayers.Low), Is.Negative,
                "the SFU sorts rids a-z and reads that order as best-to-worst");
        });
    }

    // ── The layer the tile size asks for ──────────────────────────────────────

    [Test]
    public async Task A_small_tile_is_served_the_low_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);
        await service.SetSubscriberAsync(
            _key, "u04", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 120 }));

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [Camera("u00")]);

        Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Low),
            "a 120 pixel tile served 1080p is the entire bill this package exists to remove");
    }

    [Test]
    public async Task A_full_size_tile_is_served_the_full_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);
        await service.SetSubscriberAsync(
            _key, "u04", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 1080 }));

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [Camera("u00")]);

        Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.High));
    }

    /// <summary>
    /// Nobody's camera tile in a twenty-person grid is full size, so the room being ranked at all
    /// is enough to justify the middle layer for a subscriber who has never reported a size.
    /// </summary>
    [Test]
    public async Task A_subscriber_who_reported_no_tile_size_gets_the_grid_layer_in_a_ranked_room()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [Camera("u00")]);

        Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Medium));
    }

    /// <summary>
    /// The pricing model's own case: three viewers fullscreen and the rest in tiles.
    /// </summary>
    [Test]
    public async Task A_share_in_a_small_call_is_still_layered_per_viewer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 3);
        var track = TrackNaming.ScreenTrack("share-a");
        await service.RecordTracksAsync(_key, "u00", "cf-u00", [track]);

        await service.SetSubscriberAsync(
            _key, "u01", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 2160 }));
        await service.SetSubscriberAsync(
            _key, "u02", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 160 }));

        var subscribe = new VoiceTrackRef(
            VoiceTrackDirection.Subscribe, TrackName: track, MediaSessionId: "cf-u00");
        var watcher = await service.PrepareSubscribeAsync(_key, "u01", [subscribe]);
        var tile = await service.PrepareSubscribeAsync(_key, "u02", [subscribe]);

        Assert.Multiple(() =>
        {
            Assert.That(LayerOf(watcher, track), Is.EqualTo(VoiceVideoLayers.High));
            Assert.That(LayerOf(tile, track), Is.EqualTo(VoiceVideoLayers.Low));
        });
    }

    // ── Who decides ───────────────────────────────────────────────────────────

    [Test]
    public async Task A_client_asking_for_less_than_the_plan_decided_keeps_its_own_answer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(
            _key, "u04", [Camera("u00", VoiceVideoLayers.Low)]);

        Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Low),
            "a client that knows its tile is tiny before the server does is telling the truth in the "
            + "cheap direction");
    }

    [Test]
    public async Task A_client_asking_for_more_than_the_plan_decided_is_overruled()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(
            _key, "u04", [Camera("u00", VoiceVideoLayers.High)]);

        Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Medium),
            "the party being billed picks the layer, or the choice is a suggestion the payer makes "
            + "to the spender");
    }

    // ── What is deliberately left alone ───────────────────────────────────────

    [Test]
    public async Task Audio_is_never_given_a_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [
            new VoiceTrackRef(
                VoiceTrackDirection.Subscribe, TrackName: TrackNaming.Audio, MediaSessionId: "cf-u01"),
        ]);

        Assert.That(LayerOf(decision, TrackNaming.Audio), Is.Null,
            "Opus is not simulcast, and asking for a rid it does not have is how a subscribe stops "
            + "returning media");
    }

    [Test]
    public async Task A_publish_is_never_given_a_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.RecordTracksAsync(_key, "u04", "cf-u04", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [
            new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "camera"),
            Camera("u00"),
        ]);

        Assert.That(
            decision.Tracks.Single(t => t.Direction == VoiceTrackDirection.Publish).Layer, Is.Null,
            "a layer is a statement about how somebody else's video is rendered and means nothing "
            + "on the way out");
    }

    [Test]
    public async Task Switching_the_preferred_rid_off_leaves_every_track_exactly_as_it_arrived()
    {
        var service = ServiceFor(Options with { SendPreferredRid = false });
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);

        var tracks = new[] { Camera("u00") };
        var decision = await service.PrepareSubscribeAsync(_key, "u04", tracks);

        Assert.Multiple(() =>
        {
            Assert.That(LayerOf(decision, "camera"), Is.Null);
            Assert.That(decision.Tracks, Is.SameAs(tracks),
                "the escape hatch has to be a genuine no-op, not a rebuild that happens to agree");
        });
    }

    [Test]
    public async Task A_room_with_no_plan_at_all_leaves_the_request_untouched()
    {
        var service = new VoiceRoomService(_h.Rooms, _h.Announcer);
        await PopulateAsync(service, _key, 5);

        var tracks = new[] { Camera("u00") };
        var decision = await service.PrepareSubscribeAsync(_key, "u04", tracks);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Unplanned, Is.Empty);
            Assert.That(decision.Tracks, Is.SameAs(tracks));
        });
    }

    // ── Enforcement and layers are one read ───────────────────────────────────

    [Test]
    public async Task An_unplanned_pull_is_refused_and_a_planned_one_is_layered_in_the_same_answer()
    {
        var service = ServiceFor(Options with { Enforce = true });
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [
            Camera("u01"),
            new VoiceTrackRef(
                VoiceTrackDirection.Subscribe, TrackName: TrackNaming.Audio, MediaSessionId: "cf-u03"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Unplanned, Is.EqualTo(new[] { TrackNaming.Audio }),
                "u03 is not ranked and not pinned, so pulling them is the client ignoring its set");
            Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Medium));
        });
    }

    // ── The switch an operator reaches for ────────────────────────────────────

    /// <summary>
    /// Both names work, because the specs settled on the short one after the long one shipped and an
    /// operator reaching for a switch during an incident must not find that the name in the document
    /// does nothing.
    /// </summary>
    [TestCase("VOICE_ENFORCE_SUBSCRIPTION_PLAN")]
    [TestCase("VOICE_ENFORCE")]
    public void Enforcement_can_be_switched_off_by_either_name(string variable)
    {
        try
        {
            Environment.SetEnvironmentVariable(variable, "false");
            Assert.That(VoiceSubscriptionOptions.FromEnvironment().Enforce, Is.False);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// The two halves of this package default differently, and the asymmetry is the point.
    /// </summary>
    [Test]
    public void Layer_selection_is_on_by_default_and_enforcement_waits_for_the_clients()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VoiceSubscriptionOptions.Default.Enforce, Is.False,
                "refusing a pull that released clients still make turns a saving into a retry loop");
            Assert.That(VoiceSubscriptionOptions.Default.SendPreferredRid, Is.True,
                "layer selection needs nothing from the client, so there is nothing to wait for");
        });
    }

    /// <summary>
    /// The switch governs enforcement and metering belief together, so this is also the assertion
    /// that a deployment which has turned it off is charged for what it actually sends.
    /// </summary>
    [Test]
    public async Task An_unenforced_plan_refuses_nothing_and_still_chooses_layers()
    {
        var service = ServiceFor(Options with { Enforce = false });
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", ["camera"]);

        var decision = await service.PrepareSubscribeAsync(_key, "u04", [
            Camera("u01"),
            new VoiceTrackRef(
                VoiceTrackDirection.Subscribe, TrackName: TrackNaming.Audio, MediaSessionId: "cf-u03"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Unplanned, Is.Empty);
            Assert.That(LayerOf(decision, "camera"), Is.EqualTo(VoiceVideoLayers.Medium),
                "layer selection costs a client nothing it was promised, so it does not wait on the "
                + "enforcement switch");
        });
    }
}
