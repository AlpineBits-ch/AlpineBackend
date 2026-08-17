using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Voice.Tests.Rooms;

/// <summary>The reliability contract, asserted against both room kinds by the same tests.</summary>
[TestFixture(VoiceRoomKind.Channel, TestName = "Backfill (guild channel)")]
[TestFixture(VoiceRoomKind.Call, TestName = "Backfill (direct call)")]
public class VoiceBackfillTests(string kind)
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";

    private VoiceTestHarness _h = null!;
    private VoiceRoomKey _key;

    [SetUp]
    public void SetUp()
    {
        _h = new VoiceTestHarness();
        _key = new VoiceRoomKey(kind, "room-1");
    }

    private string Event(string name) => (kind == VoiceRoomKind.Call ? "call." : "guild.voice.") + name;

    /// <summary>The incarnation a healthy client would be holding.</summary>
    private static string? InstanceOf(VoiceRoom? room) => room?.InstanceId;

    // ── Versioning: the gap-detection mechanism ───────────────────────────────

    [Test]
    public async Task Every_mutation_advances_the_version()
    {
        var afterJoin = await _h.Service.JoinAsync(_key, Alice, "device-1");
        var afterSecond = await _h.Service.JoinAsync(_key, Bob, "device-2");
        var afterMute = await _h.Service.SetMuteAsync(_key, Alice, true, serverForced: false);

        Assert.Multiple(() =>
        {
            Assert.That(afterJoin.Version, Is.GreaterThan(0));
            Assert.That(afterSecond.Version, Is.GreaterThan(afterJoin.Version));
            Assert.That(afterMute!.Version, Is.GreaterThan(afterSecond.Version));
        });
    }

    [Test]
    public async Task Every_announced_event_carries_the_room_version()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        _h.ClearSends();

        await _h.Service.SetMuteAsync(_key, Alice, true, serverForced: false);

        var sends = _h.SendsOf(Event(VoiceEvents.MuteChanged));
        Assert.That(sends, Is.Not.Empty, "the change must be announced at all");
        Assert.That(sends.All(s => s.Version is > 0), Is.True,
            "an event without a version is one a client cannot detect having missed");
    }

    [Test]
    public async Task Every_announced_event_names_its_room()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        _h.ClearSends();

        await _h.Service.SetMuteAsync(_key, Alice, true, serverForced: false);

        var field = kind == VoiceRoomKind.Call ? "callId" : "channelId";
        var envelope = _h.SendsOf(Event(VoiceEvents.MuteChanged)).First().Envelope!;
        Assert.That(envelope[field], Is.EqualTo("room-1"));
    }

    // ── The snapshot must be sufficient on its own ────────────────────────────

    [Test]
    public async Task A_joiner_is_handed_the_full_state_without_waiting_for_any_event()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        _h.ClearSends();

        await _h.Service.JoinAsync(_key, Bob, "device-2");

        var snapshot = _h.SendsTo(Bob).Single(s => s.Method == Event(VoiceEvents.Snapshot)).Snapshot!;
        var alice = snapshot.Participants.Single(p => p.UserId == Alice);

        Assert.Multiple(() =>
        {
            Assert.That(alice.MediaSessionId, Is.EqualTo("cf-alice"));
            Assert.That(alice.AudioTrackName, Is.EqualTo("audio"),
                "withholding the media handles is what made HTTP catch-up incapable of restoring a "
                + "subscription, and is the reason the call flow needed a bespoke replay");
            Assert.That(alice.PublishState, Is.EqualTo(nameof(VoicePublishState.Publishing)));
        });
    }

    [Test]
    public async Task A_participant_who_only_opened_a_session_is_not_advertised_as_pullable()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");

        var room = await _h.Rooms.LoadAsync(_key);
        var snapshot = VoiceRoomSnapshot.From(room!);
        var alice = snapshot.Participants.Single(p => p.UserId == Alice);

        Assert.Multiple(() =>
        {
            Assert.That(alice.PublishState, Is.EqualTo(nameof(VoicePublishState.Joined)));
            Assert.That(alice.MediaSessionId, Is.Null,
                "announcing a session with no track hands the joiner a pair Cloudflare has nothing "
                + "behind, and the client's per-user dedupe guard then burns on the failure");
            Assert.That(alice.AudioTrackName, Is.Null);
        });
    }

    /// <summary>The half of the camera fix that was left undone.</summary>
    [Test]
    public async Task A_camera_is_in_the_snapshot_so_a_missed_announcement_is_recoverable()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        // Published on a session of its own, which is what a desktop client sending video from a
        // second process does - so the microphone session is the wrong handle to pull it by.
        await _h.Service.RecordTracksAsync(_key, Alice, "cf-alice#camera", ["camera"]);

        var snapshot = VoiceRoomSnapshot.From((await _h.Rooms.LoadAsync(_key))!);
        var camera = snapshot.Participants.Single(p => p.UserId == Alice).VideoTracks.Single();

        Assert.Multiple(() =>
        {
            Assert.That(camera.TrackName, Is.EqualTo("camera"));
            Assert.That(camera.MediaSessionId, Is.EqualTo("cf-alice#camera"),
                "pulling a camera from the publisher's microphone session names a track that "
                + "session does not have");
        });
    }

    [Test]
    public async Task A_participant_with_no_camera_carries_an_empty_video_track_list()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");

        var snapshot = VoiceRoomSnapshot.From((await _h.Rooms.LoadAsync(_key))!);

        Assert.That(snapshot.Participants.Single(p => p.UserId == Alice).VideoTracks, Is.Empty,
            "a microphone is not a camera, and a client rendering the list must not be handed one");
    }

    /// <summary>The mirror of <see cref="StaleShareTests.A_stopped_share_disappears_from_the_snapshot"/>:
    /// the snapshot is what clients subscribe from, so a camera left on it after it stopped is one
    /// every subscriber will fail to pull.</summary>
    [Test]
    public async Task A_closed_camera_disappears_from_the_snapshot()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        await _h.Service.RecordTracksAsync(_key, Alice, "cf-alice#camera", ["camera"]);

        await _h.Service.RecordTracksClosedAsync(_key, Alice, ["camera"]);

        var snapshot = VoiceRoomSnapshot.From((await _h.Rooms.LoadAsync(_key))!);
        Assert.That(snapshot.Participants.Single(p => p.UserId == Alice).VideoTracks, Is.Empty);
    }

    [Test]
    public async Task Publish_state_cannot_be_set_independently_of_the_handles()
    {
        // Derived, not stored - so there is no way to represent "publishing with no track".
        var participant = new VoiceParticipant { UserId = Alice, MediaSessionId = "cf-1" };
        Assert.That(participant.PublishState, Is.EqualTo(VoicePublishState.Joined));

        participant.AudioTrackName = "audio";
        Assert.That(participant.PublishState, Is.EqualTo(VoicePublishState.Publishing));

        participant.MediaSessionId = null;
        Assert.That(participant.PublishState, Is.EqualTo(VoicePublishState.Joined));
        await Task.CompletedTask;
    }

    // ── Reconciliation: convergence within one heartbeat ──────────────────────

    [Test]
    public async Task A_client_in_sync_is_left_alone()
    {
        var room = await _h.Service.JoinAsync(_key, Alice, "device-1");
        _h.ClearSends();

        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), room.Version, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.InSync));
            Assert.That(_h.Sends, Is.Empty, "a healthy heartbeat should cost nothing");
        });
    }

    [Test]
    public async Task A_client_that_missed_an_event_is_sent_the_full_state()
    {
        var room = await _h.Service.JoinAsync(_key, Alice, "device-1");
        var staleVersion = room.Version;
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        _h.ClearSends();

        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), staleVersion, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent));
            Assert.That(_h.SendsTo(Alice).Any(s => s.Method == Event(VoiceEvents.Snapshot)), Is.True);
        });
    }

    /// <summary>The core repair.</summary>
    [Test]
    public async Task A_server_that_lost_a_publish_is_corrected_by_the_next_heartbeat()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        var room = (await _h.Rooms.LoadAsync(_key))!;
        _h.ClearSends();

        // Alice is really publishing; the server thinks she is not.
        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), room.Version, "cf-alice", "audio");

        var after = (await _h.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.Repaired));
            Assert.That(after.Find(Alice)!.PublishState, Is.EqualTo(VoicePublishState.Publishing));
            Assert.That(after.Find(Alice)!.MediaSessionId, Is.EqualTo("cf-alice"));
            Assert.That(after.Version, Is.GreaterThan(room.Version),
                "a repair is a state change, so peers must be able to detect it like any other");
        });
    }

    [Test]
    public async Task A_repair_that_changes_pullability_tells_the_other_participants()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        var room = (await _h.Rooms.LoadAsync(_key))!;
        _h.ClearSends();

        await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), room.Version, "cf-alice", "audio");

        Assert.That(_h.SendsTo(Bob).Any(s => s.Method == Event(VoiceEvents.Resync)), Is.True,
            "Bob could not previously subscribe to Alice and has no other way to learn that changed");
    }

    [Test]
    public async Task A_stale_server_record_of_a_stopped_publish_is_also_corrected()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        var room = (await _h.Rooms.LoadAsync(_key))!;
        _h.ClearSends();

        // Alice stopped publishing; the server still advertises her.
        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), room.Version, null, null);

        var repaired = (await _h.Rooms.LoadAsync(_key))!.Find(Alice)!;
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.Repaired));
            Assert.That(repaired.PublishState, Is.EqualTo(VoicePublishState.Joined));
        });
    }

    // ── State loss ────────────────────────────────────────────────────────────

    [Test]
    public async Task A_heartbeat_for_a_room_that_no_longer_exists_asks_the_client_to_rejoin()
    {
        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), 5, "cf-alice", "audio");

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.RoomGone));
            var resync = _h.SendsTo(Alice).Single(s => s.Method == Event(VoiceEvents.Resync));
            Assert.That(resync.Envelope!["reason"], Is.EqualTo("roomGone"));
        });
    }

    /// <summary>A client cannot talk its way back into a room it was removed from.</summary>
    [Test]
    public async Task A_removed_participant_cannot_readd_themselves_by_heartbeating()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.LeaveAsync(_key, Alice);
        _h.ClearSends();

        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), 0, "cf-alice", "audio");

        var room = (await _h.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.RoomGone));
            Assert.That(room.Find(Alice), Is.Null,
                "re-admitting on the client's own say-so would readmit anyone kicked, banned or "
                + "denied Connect");
        });
    }

    [Test]
    public async Task Liveness_is_recorded_even_when_the_room_is_gone()
    {
        await _h.Reconciler.HeartbeatAsync(Alice, _key, InstanceOf(await _h.Rooms.LoadAsync(_key)), 0, null, null);

        Assert.That(await _h.Cache.GetStringAsync(VoiceReconciler.LivenessKey(Alice, null)), Is.Not.Null,
            "dropping liveness for a disagreement would let the sweep evict a healthy participant");
    }

    // ── Membership gate on state changes ──────────────────────────────────────

    [Test]
    public async Task A_non_participant_cannot_broadcast_state_into_a_room()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        _h.ClearSends();

        var result = await _h.Service.SetMuteAsync(_key, "stranger", true, serverForced: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(_h.Sends, Is.Empty,
                "the call-side handlers used to broadcast here without ever checking membership");
        });
    }

    [Test]
    public async Task Camera_and_speaking_relays_are_also_gated_on_membership()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        _h.ClearSends();

        var camera = await _h.Service.SetCameraAsync(_key, "stranger", true);
        var speaking = await _h.Service.SetSpeakingAsync(_key, "stranger", true);

        Assert.Multiple(() =>
        {
            Assert.That(camera, Is.Null);
            Assert.That(speaking, Is.Null);
            Assert.That(_h.Sends, Is.Empty);
        });
    }
}
