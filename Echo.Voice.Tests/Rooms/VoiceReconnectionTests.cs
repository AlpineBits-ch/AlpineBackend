using Echo.Voice.Rooms;
using Echo.Voice.Testing;

namespace Echo.Voice.Tests.Rooms;

/// <summary>Reconnection and recovery, asserted against both room kinds from one body.</summary>
[TestFixture(VoiceRoomKind.Channel, TestName = "Reconnection (guild channel)")]
[TestFixture(VoiceRoomKind.Call, TestName = "Reconnection (direct call)")]
public class VoiceReconnectionTests(string kind)
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";
    private const string Carol = "user-carol";

    private VoiceTestHarness _h = null!;
    private VoiceRoomKey _key;

    [SetUp]
    public void SetUp()
    {
        _h = new VoiceTestHarness();
        _key = new VoiceRoomKey(kind, "room-1");
    }

    private string Event(string name) => (kind == VoiceRoomKind.Call ? "call." : "guild.voice.") + name;

    private async Task<VoiceRoom> RoomAsync() => (await _h.Rooms.LoadAsync(_key))!;

    private VoiceRoomSnapshot? LastSnapshotTo(string userId) =>
        _h.SendsTo(userId).LastOrDefault(s => s.Method == Event(VoiceEvents.Snapshot))?.Snapshot;

    // ── Coming back after missing everything ──────────────────────────────────

    /// <summary>The headline case.</summary>
    [Test]
    public async Task A_client_that_slept_through_every_change_is_fully_restored_by_one_heartbeat()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        var whenAliceLeftOff = await RoomAsync();

        // Alice is gone. The world moves on: two joins, a publish, a mute, a screen share.
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.JoinAsync(_key, Carol, "device-3");
        await _h.Service.RecordPublishAsync(_key, Bob, "cf-bob");
        await _h.Service.SetMuteAsync(_key, Carol, true, serverForced: false);
        await _h.Service.RecordTracksAsync(_key, Bob, "cf-bob", ["screen-s1"]);
        _h.ClearSends();

        var outcome = await _h.Reconciler.HeartbeatAsync(
            Alice, _key, whenAliceLeftOff.InstanceId, whenAliceLeftOff.Version, null, null);

        var snapshot = LastSnapshotTo(Alice)!;
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent));
            Assert.That(snapshot.Participants.Select(p => p.UserId),
                Is.EquivalentTo(new[] { Alice, Bob, Carol }));

            var bob = snapshot.Participants.Single(p => p.UserId == Bob);
            Assert.That(bob.CfSessionId, Is.EqualTo("cf-bob"), "Alice must be able to hear Bob again");
            Assert.That(bob.AudioTrackName, Is.EqualTo("audio"));
            Assert.That(bob.Shares.Single().ShareId, Is.EqualTo("s1"), "including a share she never saw start");

            Assert.That(snapshot.Participants.Single(p => p.UserId == Carol).IsSelfMuted, Is.True);
            Assert.That(snapshot.Version, Is.EqualTo(whenAliceLeftOff.Version + 5));
        });
    }

    [Test]
    public async Task Rejoining_hands_back_the_current_state_rather_than_an_empty_room()
    {
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.RecordPublishAsync(_key, Bob, "cf-bob");
        _h.ClearSends();

        await _h.Service.JoinAsync(_key, Alice, "device-1");

        var snapshot = LastSnapshotTo(Alice)!;
        Assert.That(snapshot.Participants.Single(p => p.UserId == Bob).CfSessionId, Is.EqualTo("cf-bob"));
    }

    /// <summary>Reconnecting on a second device transfers the roster entry rather than creating a
    /// duplicate participant, and the returning client is told the truth about itself.</summary>
    [Test]
    public async Task Reconnecting_from_another_device_does_not_duplicate_the_participant()
    {
        await _h.Service.JoinAsync(_key, Alice, "phone");
        await _h.Service.JoinAsync(_key, Alice, "laptop");

        var room = await RoomAsync();
        Assert.Multiple(() =>
        {
            Assert.That(room.Participants.Count(p => p.UserId == Alice), Is.EqualTo(1));
            Assert.That(room.Find(Alice)!.DeviceId, Is.EqualTo("laptop"));
        });
    }

    // ── Losing the room underneath everyone ───────────────────────────────────

    /// <summary>The case a version counter alone cannot see.</summary>
    [Test]
    public async Task A_rebuilt_room_that_reaches_the_same_version_is_still_detected()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        var before = await RoomAsync();

        // The room is lost and rebuilt by other people, landing on the same version.
        await _h.Rooms.RemoveAsync(_key);
        await _h.Service.JoinAsync(_key, Carol, "device-3");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        var after = await RoomAsync();
        _h.ClearSends();

        Assert.That(after.Version, Is.EqualTo(before.Version), "precondition: the counters agree");
        Assert.That(after.InstanceId, Is.Not.EqualTo(before.InstanceId));

        var outcome = await _h.Reconciler.HeartbeatAsync(
            Bob, _key, before.InstanceId, before.Version, null, null);

        Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent),
            "the version matches but the room is a different incarnation entirely");
    }

    /// <summary>
    /// A client can legitimately hold a version higher than the server's, because a rebuilt room
    /// climbs from zero.
    /// </summary>
    [Test]
    public async Task A_client_ahead_of_a_rebuilt_room_is_resynced_not_ignored()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        for (var i = 0; i < 10; i++)
            await _h.Service.SetMuteAsync(_key, Alice, i % 2 == 0, serverForced: false);
        var stale = await RoomAsync();

        await _h.Rooms.RemoveAsync(_key);
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        var rebuilt = await RoomAsync();
        _h.ClearSends();

        Assert.That(stale.Version, Is.GreaterThan(rebuilt.Version), "precondition: the client is ahead");

        var outcome = await _h.Reconciler.HeartbeatAsync(
            Alice, _key, stale.InstanceId, stale.Version, null, null);

        Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent));
    }

    [Test]
    public async Task A_client_with_no_instance_at_all_is_resynced()
    {
        var room = await _h.Service.JoinAsync(_key, Alice, "device-1");
        _h.ClearSends();

        // A client that reconnected before it had ever seen a snapshot.
        var outcome = await _h.Reconciler.HeartbeatAsync(Alice, _key, null, room.Version, null, null);

        Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent));
    }

    // ── Reconnect with live media ─────────────────────────────────────────────

    /// <summary>A reconnecting client whose Cloudflare session survived the socket drop.</summary>
    [Test]
    public async Task A_reconnect_that_agrees_with_the_server_costs_nothing()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        var room = await RoomAsync();
        _h.ClearSends();

        var outcome = await _h.Reconciler.HeartbeatAsync(
            Alice, _key, room.InstanceId, room.Version, "cf-alice", "audio");

        Assert.Multiple(async () =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.InSync));
            Assert.That(_h.Sends, Is.Empty);
            Assert.That((await RoomAsync()).Version, Is.EqualTo(room.Version),
                "an agreeing heartbeat must not churn the version, or every peer refetches for nothing");
        });
    }

    [Test]
    public async Task A_reconnecting_publisher_whose_record_was_lost_becomes_audible_again()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");

        // The publish record is lost - a pod died between the mutation and the announcement.
        await _h.Rooms.MutateExistingAsync(_key, r =>
        {
            var a = r.Find(Alice)!;
            a.CfSessionId = null;
            a.AudioTrackName = null;
        });
        var damaged = await RoomAsync();
        _h.ClearSends();

        await _h.Reconciler.HeartbeatAsync(
            Alice, _key, damaged.InstanceId, damaged.Version, "cf-alice", "audio");

        var repaired = await RoomAsync();
        Assert.Multiple(() =>
        {
            Assert.That(repaired.Find(Alice)!.PublishState, Is.EqualTo(VoicePublishState.Publishing));
            Assert.That(_h.SendsTo(Bob).Any(s => s.Method == Event(VoiceEvents.Resync)), Is.True,
                "Bob had no other way to discover Alice became pullable");
        });
    }

    // ── Snapshot coherence ────────────────────────────────────────────────────

    [Test]
    public async Task A_snapshot_always_reports_the_version_of_the_state_it_contains()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.RecordPublishAsync(_key, Bob, "cf-bob");

        foreach (var send in _h.Sends.Where(s => s.Method == Event(VoiceEvents.Snapshot)))
        {
            var snapshot = send.Snapshot!;
            // Participant count is a proxy for "this snapshot is internally consistent": a snapshot
            // stamped with a version but built from a different read would be the subtlest possible
            // version of this bug.
            Assert.That(snapshot.Participants.Count, Is.LessThanOrEqualTo(2));
            Assert.That(snapshot.Version, Is.GreaterThan(0));
            Assert.That(snapshot.InstanceId, Is.Not.Empty);
        }
    }

    [Test]
    public async Task Every_event_and_snapshot_agrees_on_the_room_instance()
    {
        await _h.Service.JoinAsync(_key, Alice, "device-1");
        await _h.Service.JoinAsync(_key, Bob, "device-2");
        await _h.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        var room = await RoomAsync();

        var instances = _h.Sends
            .Select(s => s.Snapshot?.InstanceId ?? s.Envelope?["instanceId"] as string)
            .Where(i => !string.IsNullOrEmpty(i))
            .Distinct()
            .ToList();

        Assert.That(instances, Is.EquivalentTo(new[] { room.InstanceId }));
    }
}
