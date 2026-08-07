using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// Two service instances, one room, participants spread across both - the real deployment.
/// </summary>
[TestFixture(VoiceRoomKind.Channel, TestName = "Multi-instance (guild channel)")]
[TestFixture(VoiceRoomKind.Call, TestName = "Multi-instance (direct call)")]
public class VoiceMultiInstanceTests(string kind)
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";

    private VoiceTestHarness _pod1 = null!;
    private VoiceTestHarness _pod2 = null!;
    private List<CapturedSend> _backplane = null!;
    private VoiceRoomKey _key;

    [SetUp]
    public void SetUp()
    {
        // One Redis, one lock, one backplane, two processes.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IDistributedLockService locks = new VoiceTestHarness.SerializingLockService();
        _backplane = [];

        _pod1 = new VoiceTestHarness(locks, cache, _backplane);
        _pod2 = new VoiceTestHarness(locks, cache, _backplane);
        _key = new VoiceRoomKey(kind, "room-1");
    }

    private string Event(string name) => (kind == VoiceRoomKind.Call ? "call." : "guild.voice.") + name;

    private List<CapturedSend> SendsTo(string userId) =>
        _backplane.Where(s => s.Target == $"user:{userId}").ToList();

    private VoiceRoomSnapshot? LastSnapshotTo(string userId) =>
        SendsTo(userId).LastOrDefault(s => s.Method == Event(VoiceEvents.Snapshot))?.Snapshot;

    // ── Both pods see one room ────────────────────────────────────────────────

    [Test]
    public async Task A_join_on_one_instance_is_visible_from_the_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");

        var seenFromPod2 = await _pod2.Rooms.LoadAsync(_key);

        Assert.That(seenFromPod2!.Find(Alice), Is.Not.Null);
    }

    [Test]
    public async Task Participants_on_different_instances_share_one_version_sequence()
    {
        var v1 = await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        var v2 = await _pod2.Service.JoinAsync(_key, Bob, "device-2");
        var v3 = await _pod1.Service.SetMuteAsync(_key, Alice, true, serverForced: false);

        Assert.Multiple(() =>
        {
            Assert.That(v1.Version, Is.EqualTo(1));
            Assert.That(v2.Version, Is.EqualTo(2), "the version is a property of the room, not of a pod");
            Assert.That(v3!.Version, Is.EqualTo(3));
            Assert.That(v1.InstanceId, Is.EqualTo(v2.InstanceId), "and so is the incarnation");
        });
    }

    /// <summary>
    /// The scenario the whole thing exists for: Alice is served by pod 1, Bob by pod 2, and Bob has
    /// to be able to hear Alice.
    /// </summary>
    [Test]
    public async Task A_publisher_on_one_instance_is_pullable_by_a_peer_on_the_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod2.Service.JoinAsync(_key, Bob, "device-2");
        _backplane.Clear();

        await _pod1.Service.RecordPublishAsync(_key, Alice, "cf-alice");

        var announced = SendsTo(Bob).Single(s => s.Method == Event(VoiceEvents.ParticipantJoined));
        Assert.Multiple(() =>
        {
            Assert.That(announced.Envelope!["mediaSessionId"], Is.EqualTo("cf-alice"));
            Assert.That(announced.Envelope!["audioTrackName"], Is.EqualTo("audio"));
        });
    }

    [Test]
    public async Task A_joiner_on_one_instance_is_told_about_a_publisher_on_the_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod1.Service.RecordPublishAsync(_key, Alice, "cf-alice");
        _backplane.Clear();

        await _pod2.Service.JoinAsync(_key, Bob, "device-2");

        var alice = LastSnapshotTo(Bob)!.Participants.Single(p => p.UserId == Alice);
        Assert.That(alice.MediaSessionId, Is.EqualTo("cf-alice"));
    }

    // ── Concurrent writes from different instances ────────────────────────────

    [Test]
    public async Task Simultaneous_joins_from_two_instances_both_land()
    {
        await Task.WhenAll(
            _pod1.Service.JoinAsync(_key, Alice, "device-1"),
            _pod2.Service.JoinAsync(_key, Bob, "device-2"));

        var room = (await _pod1.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(room.Participants.Select(p => p.UserId), Is.EquivalentTo(new[] { Alice, Bob }));
            Assert.That(room.Version, Is.EqualTo(2));
        });
    }

    /// <summary>The lost-update race, across processes this time.</summary>
    [Test]
    public async Task Interleaved_writes_from_two_instances_do_not_lose_each_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod2.Service.JoinAsync(_key, Bob, "device-2");

        await Task.WhenAll(
            _pod1.Service.RecordPublishAsync(_key, Alice, "cf-alice"),
            _pod2.Service.RecordPublishAsync(_key, Bob, "cf-bob"),
            _pod1.Service.SetMuteAsync(_key, Alice, true, serverForced: false),
            _pod2.Service.SetDeafenAsync(_key, Bob, true, serverForced: false));

        var room = (await _pod1.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(room.Find(Alice)!.MediaSessionId, Is.EqualTo("cf-alice"));
            Assert.That(room.Find(Bob)!.MediaSessionId, Is.EqualTo("cf-bob"));
            Assert.That(room.Find(Alice)!.IsSelfMuted, Is.True);
            Assert.That(room.Find(Bob)!.IsSelfDeafened, Is.True);
            Assert.That(room.Version, Is.EqualTo(6));
        });
    }

    [Test]
    public async Task A_room_created_on_one_instance_is_never_recreated_by_the_other()
    {
        await Task.WhenAll(
            _pod1.Service.JoinAsync(_key, Alice, "device-1"),
            _pod2.Service.JoinAsync(_key, Bob, "device-2"));

        var fromPod1 = await _pod1.Rooms.LoadAsync(_key);
        var fromPod2 = await _pod2.Rooms.LoadAsync(_key);

        Assert.That(fromPod1!.InstanceId, Is.EqualTo(fromPod2!.InstanceId),
            "two incarnations would make every client resync forever, each pod undoing the other");
    }

    // ── Reconnecting onto a different instance ────────────────────────────────

    /// <summary>
    /// A client reconnects and lands on the other pod, which is the normal case behind a load
    /// balancer.
    /// </summary>
    [Test]
    public async Task A_heartbeat_answered_by_the_other_instance_reconciles_correctly()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod1.Service.JoinAsync(_key, Bob, "device-2");
        var known = (await _pod1.Rooms.LoadAsync(_key))!;

        // Alice was away; Bob published via pod 1. Alice reconnects onto pod 2.
        await _pod1.Service.RecordPublishAsync(_key, Bob, "cf-bob");
        _backplane.Clear();

        var outcome = await _pod2.Reconciler.HeartbeatAsync(
            Alice, _key, known.InstanceId, known.Version, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.SnapshotSent));
            Assert.That(LastSnapshotTo(Alice)!.Participants.Single(p => p.UserId == Bob).MediaSessionId,
                Is.EqualTo("cf-bob"));
        });
    }

    [Test]
    public async Task A_repair_on_one_instance_is_announced_to_peers_on_the_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod2.Service.JoinAsync(_key, Bob, "device-2");
        var known = (await _pod1.Rooms.LoadAsync(_key))!;
        _backplane.Clear();

        // Alice's publish record was lost; she heartbeats onto pod 2 and it is repaired there.
        await _pod2.Reconciler.HeartbeatAsync(
            Alice, _key, known.InstanceId, known.Version, "cf-alice", "audio");

        Assert.Multiple(async () =>
        {
            Assert.That(SendsTo(Bob).Any(s => s.Method == Event(VoiceEvents.Resync)), Is.True,
                "Bob is on the other pod and still has to learn Alice became pullable");
            Assert.That((await _pod1.Rooms.LoadAsync(_key))!.Find(Alice)!.PublishState,
                Is.EqualTo(VoicePublishState.Publishing));
        });
    }

    [Test]
    public async Task A_leave_on_one_instance_is_seen_by_the_other()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod2.Service.JoinAsync(_key, Bob, "device-2");

        await _pod2.Service.LeaveAsync(_key, Bob);

        Assert.That((await _pod1.Rooms.LoadAsync(_key))!.Find(Bob), Is.Null);
    }

    // ── Event ordering across instances ───────────────────────────────────────

    /// <summary>
    /// Two pods emit into the same backplane, so a client can see interleaved events.
    /// </summary>
    [Test]
    public async Task Events_from_both_instances_carry_distinct_ordered_versions()
    {
        await _pod1.Service.JoinAsync(_key, Alice, "device-1");
        await _pod2.Service.JoinAsync(_key, Bob, "device-2");
        _backplane.Clear();

        await _pod1.Service.SetMuteAsync(_key, Alice, true, serverForced: false);
        await _pod2.Service.SetMuteAsync(_key, Bob, true, serverForced: false);
        await _pod1.Service.SetMuteAsync(_key, Alice, false, serverForced: false);

        var versions = _backplane
            .Where(s => s.Method == Event(VoiceEvents.MuteChanged))
            .Select(s => s.Version!.Value)
            .Distinct()
            .Order()
            .ToList();

        Assert.That(versions, Is.EqualTo(new long[] { 3, 4, 5 }));
    }
}
