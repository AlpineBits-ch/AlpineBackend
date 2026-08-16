using Echo.Voice.Rooms;
using Echo.Voice.Testing;

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

    private string Event(string name) => (kind == VoiceRoomKind.Call ? "call." : "guild.voice.") + name;

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

    // ── When the publisher never says anything at all ─────────────────────────

    [Test]
    public async Task Media_the_sfu_says_is_gone_is_dropped_from_the_roster()
    {
        await PublishedShareAsync();

        // No SetStreamingAsync and no close-tracks: the publisher's stop never arrived.
        await _h.Service.RecordTracksMissingAsync(_key, [ScreenTrack, ScreenAudioTrack]);

        var me = (await _h.Rooms.LoadAsync(_key))!.Find(Publisher)!;
        Assert.Multiple(() =>
        {
            Assert.That(me.ActiveScreenShares, Is.Empty);
            Assert.That(me.IsStreaming, Is.False, "the flag must not outlive the list");
        });
    }

    [Test]
    public async Task Everyone_is_told_including_the_publisher()
    {
        await PublishedShareAsync();
        _h.ClearSends();

        await _h.Service.RecordTracksMissingAsync(_key, [ScreenTrack, ScreenAudioTrack]);

        var closed = _h.SendsOf(Event(VoiceEvents.TrackClosed));
        Assert.That(closed, Is.Not.Empty, "peers hold a dead track until they are told otherwise");
        Assert.That(closed.Any(s => s.Target.Contains(Publisher)), Is.True,
            "the publisher is the one client that still believes it is sharing");
    }

    [Test]
    public async Task Another_share_by_the_same_publisher_survives()
    {
        await PublishedShareAsync();
        await _h.Service.SetStreamingAsync(_key, Publisher, isStreaming: true, "second-share");
        await _h.Service.RecordTracksAsync(_key, Publisher, "cf-publisher", ["screen-second-share"]);

        await _h.Service.RecordTracksMissingAsync(_key, [ScreenTrack, ScreenAudioTrack]);

        var me = (await _h.Rooms.LoadAsync(_key))!.Find(Publisher)!;
        Assert.Multiple(() =>
        {
            Assert.That(me.ActiveScreenShares.Select(s => s.ShareId), Is.EqualTo(new[] { "second-share" }));
            Assert.That(me.IsStreaming, Is.True);
        });
    }

    [Test]
    public async Task A_track_nobody_was_publishing_changes_nothing()
    {
        await PublishedShareAsync();
        _h.Sends.Clear();

        await _h.Service.RecordTracksMissingAsync(_key, ["screen-never-existed"]);

        var me = (await _h.Rooms.LoadAsync(_key))!.Find(Publisher)!;
        Assert.Multiple(() =>
        {
            Assert.That(me.ActiveScreenShares, Is.Not.Empty, "the live share must be untouched");
            Assert.That(_h.SendsOf(Event(VoiceEvents.TrackClosed)), Is.Empty,
                "announcing a track nobody owns would have peers drop media that is still live");
        });
    }

    /// <summary>
    /// The microphone is deliberately out of scope: every participant's track is called "audio",
    /// so the name identifies nobody, and pruning on it would silence an arbitrary person.
    /// </summary>
    [Test]
    public async Task A_missing_microphone_track_is_left_alone()
    {
        await PublishedShareAsync();

        await _h.Service.RecordTracksMissingAsync(_key, ["audio"]);

        var me = (await _h.Rooms.LoadAsync(_key))!.Find(Publisher)!;
        Assert.That(me.MediaSessionId, Is.EqualTo("cf-publisher"));
    }

}
