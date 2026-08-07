using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Messaging.Application.Handler.Realtime;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>
/// Covers <see cref="CallVoiceStateHandler"/>, which replaces the five near-identical handlers that
/// used to relay camera, mute, speaking and screen-share state for a call.
/// </summary>
[TestFixture]
public class CallRealtimeCommandHandlersTests
{
    private const string CallId = "call-1";
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";
    private const string Stranger = "user-stranger";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private StreamViewerStore _viewers = null!;
    private VoiceRoomService _voice = null!;
    private CallVoiceStateHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        var locks = new FakeDistributedLockService();
        _viewers = new StreamViewerStore(locks, _cache);
        _voice = VoiceTestHarness.ServiceFor(_cache, locks, _hub);
        _handler = new CallVoiceStateHandler();
    }

    private static VoiceRoomKey Room => VoiceRoomKey.Call(CallId);

    /// <summary>Puts the given users in the call's voice room, which is what membership now means.</summary>
    private Task SeedRoomAsync(params string[] userIds) =>
        VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = CallId,
            Kind = VoiceRoomKind.Call,
            Participants = userIds.Select(id => new VoiceParticipant { UserId = id }).ToList(),
        });

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    private List<string> TargetsOf(string method) =>
        HubClients.Sends.Where(s => s.Method == method).Select(s => s.Target).ToList();

    // ══════════════════════════════════════════════════════════════════════════ The relay still
    // works ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Camera_BroadcastsToOtherParticipants_ExcludingSelf()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallCameraCommand(Alice, CallId, true), _voice);

        Assert.That(TargetsOf("call.CameraChanged"), Is.EqualTo(new[] { $"users:{Bob}" }));
    }

    [Test]
    public async Task Mute_BroadcastsToOtherParticipants_AndIsRecorded()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallMuteCommand(Alice, CallId, true), _voice);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, Room);
        Assert.Multiple(() =>
        {
            Assert.That(TargetsOf("call.MuteChanged"), Is.EqualTo(new[] { $"users:{Bob}" }));
            Assert.That(room!.Find(Alice)!.IsSelfMuted, Is.True,
                "mute is roster state, not just a relay - a client joining later has to see it");
        });
    }

    [Test]
    public async Task Speaking_BroadcastsToOtherParticipants()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallSpeakingCommand(Alice, CallId, true), _voice);

        Assert.That(TargetsOf("call.SpeakingChanged"), Is.EqualTo(new[] { $"users:{Bob}" }));
    }

    [Test]
    public async Task ScreenShareStart_BroadcastsAndMarksTheSharerStreaming()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallScreenShareStartCommand(Alice, CallId, "share-1"), _voice);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, Room);
        Assert.Multiple(() =>
        {
            Assert.That(TargetsOf("call.ScreenShareStarted"), Is.EqualTo(new[] { $"users:{Bob}" }));
            Assert.That(room!.Find(Alice)!.IsStreaming, Is.True);
        });
    }

    [Test]
    public async Task ScreenShareStop_BroadcastsAndDropsTheShareAudience()
    {
        await SeedRoomAsync(Alice, Bob);
        await _handler.Handle(new CallScreenShareStartCommand(Alice, CallId, "share-1"), _voice);
        await _viewers.WatchAsync(Room.ViewerScope, "share-1", Bob);

        await _handler.Handle(new CallScreenShareStopCommand(Alice, CallId, "share-1"), _voice, _viewers);

        var snapshot = await _viewers.SnapshotAsync(Room.ViewerScope);
        Assert.Multiple(() =>
        {
            Assert.That(TargetsOf("call.ScreenShareStopped"), Is.EqualTo(new[] { $"users:{Bob}" }));
            Assert.That(snapshot, Does.Not.ContainKey("share-1"),
                "the audience of a stopped share is undefined, not empty - a later share reusing the "
                + "id must not inherit it");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ No room, no
    // broadcast ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NoRoom_IsANoOpForEveryCommand()
    {
        await _handler.Handle(new CallCameraCommand(Alice, CallId, true), _voice);
        await _handler.Handle(new CallMuteCommand(Alice, CallId, true), _voice);
        await _handler.Handle(new CallSpeakingCommand(Alice, CallId, true), _voice);
        await _handler.Handle(new CallScreenShareStartCommand(Alice, CallId, "s"), _voice);
        await _handler.Handle(new CallScreenShareStopCommand(Alice, CallId, "s"), _voice, _viewers);

        Assert.That(HubClients.Sends, Is.Empty,
            "a call that ended or never existed has nobody to tell");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The hole these handlers used to have
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A non-participant holding a call id could previously broadcast any of these to everyone in
    /// the call.
    /// </summary>
    [Test]
    public async Task AStranger_CannotBroadcastAnyStateIntoACallTheyAreNotIn()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallCameraCommand(Stranger, CallId, true), _voice);
        await _handler.Handle(new CallMuteCommand(Stranger, CallId, true), _voice);
        await _handler.Handle(new CallSpeakingCommand(Stranger, CallId, true), _voice);
        await _handler.Handle(new CallScreenShareStartCommand(Stranger, CallId, "s"), _voice);

        Assert.That(HubClients.Sends, Is.Empty);
    }

    /// <summary>
    /// The stop handler additionally cleared the viewer table, so a stranger could wipe the audience
    /// of somebody else's live share. Gated on the membership result now.
    /// </summary>
    [Test]
    public async Task AStranger_CannotClearTheViewerTableOfALiveShare()
    {
        await SeedRoomAsync(Alice, Bob);
        await _handler.Handle(new CallScreenShareStartCommand(Alice, CallId, "share-1"), _voice);
        await _viewers.WatchAsync(Room.ViewerScope, "share-1", Bob);
        HubClients.Sends.Clear();

        await _handler.Handle(new CallScreenShareStopCommand(Stranger, CallId, "share-1"), _voice, _viewers);

        var snapshot = await _viewers.SnapshotAsync(Room.ViewerScope);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Does.ContainKey("share-1"), "the share's audience survives");
            Assert.That(HubClients.Sends, Is.Empty);
        });
    }

    [Test]
    public async Task AStrangersMuteDoesNotLandOnTheRoster()
    {
        await SeedRoomAsync(Alice, Bob);

        await _handler.Handle(new CallMuteCommand(Stranger, CallId, true), _voice);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, Room);
        Assert.That(room!.Participants.Any(p => p.UserId == Stranger), Is.False,
            "a rejected command must not conjure the sender into the roster either");
    }
}
