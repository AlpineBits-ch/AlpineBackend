using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tests.Usage;
using Echo.Voice.Tracks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// Which simulcast layer each subscriber is told to pull, which is where most of the video bill is
/// decided.
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
            await service.RecordPublishAsync(key, userId, userId);
        }
    }

    /// <summary>The layer one subscriber is told to serve a given track at, or null when the plan has
    /// nothing to say about it.</summary>
    private static async Task<string?> LayerFor(
        VoiceRoomService service, VoiceRoomKey key, string subscriber, string trackName)
    {
        var plan = await service.GetSubscriptionsAsync(key);
        return plan.For(subscriber).Tracks
            .FirstOrDefault(t => t.TrackName == trackName)?.Layer;
    }

    // ── The names themselves ──────────────────────────────────────────────────

    /// <summary>The alphabet is the quality ranking.</summary>
    [Test]
    public void Layer_names_sort_alphabetically_in_descending_quality()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                string.CompareOrdinal(VoiceVideoLayers.High, VoiceVideoLayers.Medium), Is.Negative,
                "rids sort a-z and that order is read as best-to-worst");
            Assert.That(
                string.CompareOrdinal(VoiceVideoLayers.Medium, VoiceVideoLayers.Low), Is.Negative,
                "rids sort a-z and that order is read as best-to-worst");
        });
    }

    // ── The layer the tile size asks for ──────────────────────────────────────

    [Test]
    public async Task A_small_tile_is_served_the_low_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.RecordTracksAsync(_key, "u00", "u00", ["camera"]);
        await service.SetSubscriberAsync(
            _key, "u04", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 120 }));

        Assert.That(await LayerFor(service, _key, "u04", "camera"), Is.EqualTo(VoiceVideoLayers.Low),
            "a 120 pixel tile served 1080p is the entire bill this package exists to remove");
    }

    [Test]
    public async Task A_full_size_tile_is_served_the_full_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.RecordTracksAsync(_key, "u00", "u00", ["camera"]);
        await service.SetSubscriberAsync(
            _key, "u04", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 1080 }));

        Assert.That(await LayerFor(service, _key, "u04", "camera"), Is.EqualTo(VoiceVideoLayers.High));
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
        await service.RecordTracksAsync(_key, "u00", "u00", ["camera"]);

        Assert.That(await LayerFor(service, _key, "u04", "camera"),
            Is.EqualTo(VoiceVideoLayers.Medium));
    }

    /// <summary>
    /// The pricing model's own case: some viewers fullscreen and the rest in tiles.
    /// </summary>
    [Test]
    public async Task A_share_in_a_small_call_is_still_layered_per_viewer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 3);
        var track = TrackNaming.ScreenTrack("share-a");
        await service.RecordTracksAsync(_key, "u00", "u00", [track]);

        await service.SetSubscriberAsync(
            _key, "u01", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 2160 }));
        await service.SetSubscriberAsync(
            _key, "u02", new VoiceSubscriberUpdate(TileHeights: new Dictionary<string, int> { ["u00"] = 160 }));

        Assert.Multiple(async () =>
        {
            Assert.That(await LayerFor(service, _key, "u01", track), Is.EqualTo(VoiceVideoLayers.High));
            Assert.That(await LayerFor(service, _key, "u02", track), Is.EqualTo(VoiceVideoLayers.Low));
        });
    }

    // ── What is deliberately left alone ───────────────────────────────────────

    [Test]
    public async Task Audio_is_never_given_a_layer()
    {
        var service = ServiceFor(Options);
        await PopulateAsync(service, _key, 5);
        await service.SetSpeakingAsync(_key, "u01", true);

        Assert.That(await LayerFor(service, _key, "u04", TrackNaming.Audio), Is.Null,
            "Opus is not simulcast, and asking for a rid it does not have is how a subscription stops "
            + "returning media");
    }

    // ── The switch an operator reaches for ────────────────────────────────────

    /// <summary>
    /// Both names work, because the specs settled on the short one after the long one shipped and an
    /// operator reaching for a switch during an incident must not find that the name in the document
    /// does nothing.
    /// </summary>
    [TestCase("VOICE_ENFORCE_SUBSCRIPTION_PLAN")]
    [TestCase("VOICE_ENFORCE")]
    public void The_metering_switch_can_be_set_by_either_name(string variable)
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

    /// <summary>Layer selection ships on; billing against the plan does not.</summary>
    [Test]
    public void Billing_against_the_plan_is_off_by_default()
    {
        Assert.That(VoiceSubscriptionOptions.Default.Enforce, Is.False,
            "an advisory plan describes what clients were asked to pull, not what the SFU sent");
    }
}
