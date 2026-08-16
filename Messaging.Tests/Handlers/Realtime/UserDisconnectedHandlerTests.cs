using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Handler.Realtime;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Messaging.Tests.Helpers;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>
/// Covers UserDisconnectedHandler: presence fan-out (always) plus, for VoiceHub disconnects that
/// carry a DeviceId, routing through Call.Leave the same way the explicit /leave endpoint does -
/// see the handler's own header comment.
/// </summary>
[TestFixture]
public class UserDisconnectedHandlerTests
{
    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private LockedJsonCacheStore _callStore = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);
    }

    private static FakeMessageBus BusWithEmptyProfile() => new(msg => msg switch
    {
        GetProfileByUserIdRequest => new GetProfileByUserIdResponse { Profile = new ProfileDto { Relationships = [] } },
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    private async Task SeedCall(Call call) =>
        await _cache.SetAsync(Call.GetCacheId(call.Id), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());

    [Test]
    public async Task Handle_AlwaysPublishesUserInactiveEvent()
    {
        var bus = BusWithEmptyProfile();

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1"), bus, _cache, _callStore, _hub);

        Assert.That(bus.Published.Any(p => p is UserInactiveEvent evt && evt.UserId == "user-1"), Is.True);
    }

    [Test]
    public async Task Handle_NotifiesRelationshipsOverHub_PresenceOffline()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => new GetProfileByUserIdResponse
            {
                Profile = new ProfileDto
                {
                    Relationships = [new RelationshipDto { Id = "r-1", UserId = "friend-1", Status = RelationshipStatus.Accepted }],
                },
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1"), bus, _cache, _callStore, _hub);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages.Any(m => m.Method == "presence.UserOffline"), Is.True);
    }

    [Test]
    public async Task Handle_NoActiveCall_SkipsCallLeaveEntirely()
    {
        var bus = BusWithEmptyProfile();

        // No "user-call:{userId}" entry seeded in the cache at all.
        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-1"), bus, _cache, _callStore, _hub);

        Assert.That(_cache.HasEntry("user-call:user-1"), Is.False);
    }

    [Test]
    public async Task Handle_NoDeviceId_SkipsCallLeave_ButClearsUserCallEntry()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" }],
        });

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", DeviceId: null), bus, _cache, _callStore, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry("user-call:user-1"), Is.False, "The reverse-lookup key must always be cleared");
            // Call.Leave was never invoked - status must be untouched.
            var raw = _cache.Get(Call.GetCacheId("call-1"));
            var call = JsonSerializer.Deserialize<Call>(raw!)!;
            Assert.That(call.Status, Is.EqualTo(CallStatus.Pending));
        });
    }

    [Test]
    public async Task Handle_OtherParticipantsStillConnected_PublishesParticipantLeft_CallNotEnded()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants =
            [
                new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" },
                new CallParticipant { UserId = "user-2", Status = CallStatus.Connected, ActiveDeviceId = "device-2" },
            ],
        });

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-1"), bus, _cache, _callStore, _hub);

        Assert.That(bus.Published.Any(p => p is CallParticipantLeft), Is.True);
        Assert.That(bus.Published.Any(p => p is CallEnded), Is.False,
            "Call must stay alive while another participant is still connected");
    }

    [Test]
    public async Task Handle_LastParticipantLeaves_CallEnds_NoCancelRecipients_DoesNotCallPushService()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" }],
        });

        // Single-participant call: after this user leaves, cancelRecipientIds (all participants
        // minus the initiator) is empty, so CallEndNotifier returns before ever touching
        // CallPushService/bus.InvokeAsync - BusWithEmptyProfile's responder would throw for any
        // InvokeAsync call it doesn't recognize, which doubles as proof that path wasn't taken.
        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-1"), bus, _cache, _callStore, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(bus.Published.Any(p => p is CallEnded), Is.True);
            Assert.That(_cache.HasEntry("user-call:user-1"), Is.False);

            var hubClients = (FakeHubClients)_hub.Clients;
            Assert.That(hubClients.SentMessages.Any(m => m.Method == "call.CallEnded"), Is.True);
        });
    }

    [Test]
    public async Task Handle_LeavesATrailPointingAtTheCallTheUserWasDroppedFrom()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants =
            [
                new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" },
                new CallParticipant { UserId = "user-2", Status = CallStatus.Connected, ActiveDeviceId = "device-2" },
            ],
        });

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-1"), bus, _cache, _callStore, _hub);

        // The forward index is cleared, as it always was.
        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry("user-call:user-1"), Is.False);
            Assert.That(
                System.Text.Encoding.UTF8.GetString(
                    _cache.Get(UserDisconnectedHandler.RecentCallKey("user-1"))!),
                Is.EqualTo("call-1"));
        });
    }

    [Test]
    public async Task Handle_WithNoCallAtAll_LeavesNoTrail()
    {
        var bus = BusWithEmptyProfile();

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-1"), bus, _cache, _callStore, _hub);

        Assert.That(_cache.HasEntry(UserDisconnectedHandler.RecentCallKey("user-1")), Is.False);
    }

    /// <summary>
    /// A gateway rollout produces one of these per open socket, in the same second, and every one
    /// of them looks exactly like somebody hanging up.
    /// </summary>
    [Test]
    public async Task Handle_WhenTheGatewayIsStopping_LeavesTheCallRunning()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants =
            [
                new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" },
                new CallParticipant { UserId = "user-2", Status = CallStatus.Connected, ActiveDeviceId = "device-2" },
            ],
        });

        await UserDisconnectedHandler.Handle(
            new UserDisconnected("user-1", "device-1", ServerStopping: true), bus, _cache, _callStore, _hub);

        Assert.Multiple(() =>
        {
            Assert.That(bus.Published.Any(p => p is CallParticipantLeft), Is.False);
            Assert.That(bus.Published.Any(p => p is CallEnded), Is.False);
            Assert.That(_cache.HasEntry("user-call:user-1"), Is.True,
                "the reverse-lookup key is how this user's client finds its way back into the call "
                + "when it reconnects to a surviving gateway pod");
        });
    }

    [Test]
    public async Task Handle_WhenTheGatewayIsStopping_StillReportsPresence()
    {
        var bus = BusWithEmptyProfile();

        await UserDisconnectedHandler.Handle(
            new UserDisconnected("user-1", "device-1", ServerStopping: true), bus, _cache, _callStore, _hub);

        // Presence is a claim about right now and repairs itself on the next connect, so it is on the
        // near side of the guard. Only the things that start a clock against the user are skipped.
        Assert.That(bus.Published.Any(p => p is UserInactiveEvent), Is.True);
    }

    [Test]
    public async Task Handle_DeviceMismatch_LeaveNoOps_CallUntouched()
    {
        var bus = BusWithEmptyProfile();
        _cache.SetEntry("user-call:user-1", "call-1");
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            // Active device is device-1, but the disconnect event reports device-2 (a stale
            // signal from an already-superseded device) - Call.Leave must no-op.
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Connected, ActiveDeviceId = "device-1" }],
        });

        await UserDisconnectedHandler.Handle(new UserDisconnected("user-1", "device-2"), bus, _cache, _callStore, _hub);

        Assert.That(bus.Published.Any(p => p is CallParticipantLeft), Is.False);
    }
}
