using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Transport;

namespace Echo.Voice.Tests.Rooms;

/// <summary>The incident of 2026-08-07 08:31Z, pinned.</summary>
[TestFixture(VoiceRoomKind.Channel, TestName = "Stale share (guild channel)")]
[TestFixture(VoiceRoomKind.Call, TestName = "Stale share (direct call)")]
public class StaleShareTests(string kind)
{
    private const string Publisher = "user-publisher";
    private const string Watcher = "user-watcher";
    private const string ShareId = "7c41c31c-bf05-477a-a215-879e6dd7b844";

    private VoiceTestHarness _h = null!;
    private VoiceRoomKey _key;

    [SetUp]
    public void SetUp()
    {
        _h = new VoiceTestHarness();
        _key = new VoiceRoomKey(kind, "room-1");
    }

    private static string ScreenTrack => $"screen-{ShareId}";
    private static string ScreenAudioTrack => $"screen-audio-{ShareId}";

    private async Task<VoiceRoom> PublishedShareAsync()
    {
        await _h.Service.JoinAsync(_key, Publisher, "device-p");
        await _h.Service.JoinAsync(_key, Watcher, "device-w");
        await _h.Service.RecordPublishAsync(_key, Publisher, "cf-publisher");
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: true, ShareId);
        return (await _h.Service.RecordTracksAsync(
            _key, Publisher, "cf-publisher", [ScreenTrack, ScreenAudioTrack]))!;
    }

    // ── Stopping a share removes it ───────────────────────────────────────────

    [Test]
    public async Task Stopping_a_share_removes_it_from_the_roster()
    {
        await PublishedShareAsync();

        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: false, ShareId);

        var room = (await _h.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(room.Find(Publisher)!.ActiveScreenShares, Is.Empty,
                "a share left on the roster is one every subscriber will fail to pull");
            Assert.That(room.Find(Publisher)!.IsStreaming, Is.False);
        });
    }

    [Test]
    public async Task A_stopped_share_disappears_from_the_snapshot()
    {
        await PublishedShareAsync();
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: false, ShareId);

        var snapshot = VoiceRoomSnapshot.From((await _h.Rooms.LoadAsync(_key))!);

        Assert.That(snapshot.Participants.Single(p => p.UserId == Publisher).Shares, Is.Empty,
            "the snapshot is what clients subscribe from, so advertising a dead share guarantees "
            + "a failed subscribe");
    }

    /// <summary>Stopping one share must not take another down with it, and must not clear the
    /// streaming flag while the other is still live.</summary>
    [Test]
    public async Task Stopping_one_share_leaves_another_running()
    {
        await PublishedShareAsync();
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: true, "second-share");
        await _h.Service.RecordTracksAsync(_key, Publisher, "cf-publisher", ["screen-second-share"]);

        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: false, ShareId);

        var me = (await _h.Rooms.LoadAsync(_key))!.Find(Publisher)!;
        Assert.Multiple(() =>
        {
            Assert.That(me.ActiveScreenShares.Select(s => s.ShareId), Is.EqualTo(new[] { "second-share" }));
            Assert.That(me.IsStreaming, Is.True, "they are still sharing something");
        });
    }

    // ── Stale subscribes are caught before the SFU ────────────────────────────

    [Test]
    public async Task A_subscribe_to_a_stopped_share_is_reported_stale()
    {
        await PublishedShareAsync();
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: false, ShareId);

        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: ScreenTrack)]);

        Assert.That(stale, Is.EqualTo(new[] { ScreenTrack }));
    }

    [Test]
    public async Task A_subscribe_to_a_live_share_is_not_reported_stale()
    {
        await PublishedShareAsync();

        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
        [
            new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: ScreenTrack),
            new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: ScreenAudioTrack),
        ]);

        Assert.That(stale, Is.Empty);
    }

    [Test]
    public async Task A_microphone_subscribe_to_someone_who_is_not_publishing_is_reported_stale()
    {
        await _h.Service.JoinAsync(_key, Publisher, "device-p");
        await _h.Service.JoinAsync(_key, Watcher, "device-w");

        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: "audio")]);

        Assert.That(stale, Is.EqualTo(new[] { "audio" }),
            "a session id alone is not something to pull from - the publisher has no track yet");
    }

    [Test]
    public async Task A_microphone_subscribe_to_a_live_publisher_is_fine()
    {
        await PublishedShareAsync();

        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: "audio")]);

        Assert.That(stale, Is.Empty);
    }

    [Test]
    public async Task Publishes_are_never_reported_stale()
    {
        await PublishedShareAsync();

        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
        [
            new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "audio"),
            new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "1", TrackName: "screen-brand-new"),
        ]);

        Assert.That(stale, Is.Empty,
            "the check is about what you are pulling; a publish creates the thing it names");
    }

    [Test]
    public async Task Every_subscribe_into_a_room_that_no_longer_exists_is_stale()
    {
        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-gone", TrackName: "audio")]);

        Assert.That(stale, Is.EqualTo(new[] { "audio" }));
    }

    // ── The full production sequence ──────────────────────────────────────────

    /// <summary>
    /// End to end, in the order it happened: publish a share, stop it over the hub without closing
    /// tracks, then have a watcher subscribe from a snapshot taken while it was live.
    /// </summary>
    [Test]
    public async Task The_incident_sequence_no_longer_produces_a_doomed_subscribe()
    {
        var live = await PublishedShareAsync();
        var staleSnapshot = VoiceRoomSnapshot.From(live);
        Assert.That(staleSnapshot.Participants.Single(p => p.UserId == Publisher).Shares, Is.Not.Empty,
            "precondition: the watcher's snapshot legitimately showed the share");

        // The publisher stops over the hub. It does not call close-tracks - the whole point.
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: false, ShareId);

        // The watcher acts on the snapshot it already had.
        var stale = await _h.Service.FindStaleSubscriptionsAsync(_key,
            [new VoiceTrackRef(VoiceTrackDirection.Subscribe, MediaSessionId: "cf-publisher", TrackName: ScreenTrack)]);

        Assert.That(stale, Is.EqualTo(new[] { ScreenTrack }),
            "answered from the roster in one Redis read, instead of six seconds of SFU retries "
            + "ending in a 502 that lights up the status page");
    }
}
