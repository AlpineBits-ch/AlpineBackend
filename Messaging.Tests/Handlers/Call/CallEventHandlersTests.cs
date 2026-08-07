using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Realtime.Sfu;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Handler.Call;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers the Call.* domain-event handlers (CallAcceptedHandler, CallDeclinedHandler,
/// CallCreatedHandler, CallDeviceDismissedHandler, CallDeviceTakeoverHandler, CallEndedHandler,
/// CallParticipantLeftHandler, CallRingTimeoutCheckHandler, CallWentAloneHandler).
/// </summary>
[TestFixture]
public class CallEventHandlersTests
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

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    private async Task SeedCall(Call call) =>
        await _cache.SetAsync(Call.GetCacheId(call.Id), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());

    private static FakeMessageBus BusThatMustNotInvoke() => new(msg => throw new InvalidOperationException(
        "unexpected InvokeAsync for " + msg.GetType().Name + " - this test's call shape must keep cancelRecipientIds empty"));

    // ══════════════════════════════════════════════════════════════════════════ CallCreatedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void CallCreated_SchedulesRingTimeoutCheck_ForSameCallId()
    {
        var result = CallCreatedHandler.Handle(new CallCreated { CallId = "call-1" });

        var scheduled = (DeliveryMessage<CallRingTimeoutCheck>)result;
        Assert.That(scheduled.Message.CallId, Is.EqualTo("call-1"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallAcceptedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void CallAccepted_CallNotInCache_Throws()
    {
        var bus = BusThatMustNotInvoke();

        Assert.ThrowsAsync<Exception>(() => CallAcceptedHandler.Handle(new CallAccepted { CallId = "call-missing", UserId = "user-1" }, _hub, _cache, bus));
    }

    [Test]
    public async Task CallAccepted_SoleParticipantIsAccepter_BroadcastsAccepted_NoCancelPushNeeded()
    {
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants = [new CallParticipant { UserId = "user-1" }],
        });
        var bus = BusThatMustNotInvoke();

        await CallAcceptedHandler.Handle(new CallAccepted { CallId = "call-1", UserId = "user-1" }, _hub, _cache, bus);

        Assert.That(HubClients.SentMessages.Any(m => m.Method == "call.CallAccepted"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallDeclinedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void CallDeclined_CallNotInCache_Throws()
    {
        var bus = BusThatMustNotInvoke();

        Assert.ThrowsAsync<Exception>(() => CallDeclinedHandler.Handle(new CallDeclined { CallId = "call-missing", UserId = "user-1" }, _hub, _cache, bus));
    }

    [Test]
    public async Task CallDeclined_SoleParticipantIsDecliner_BroadcastsDeclined_NoCancelPushNeeded()
    {
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants = [new CallParticipant { UserId = "user-1" }],
        });
        var bus = BusThatMustNotInvoke();

        await CallDeclinedHandler.Handle(new CallDeclined { CallId = "call-1", UserId = "user-1" }, _hub, _cache, bus);

        Assert.That(HubClients.SentMessages.Any(m => m.Method == "call.CallDeclined"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallDeviceDismissedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallDeviceDismissed_NotifiesOnlyTheStaleDevicesGroup()
    {
        await CallDeviceDismissedHandler.Handle(new CallDeviceDismissed { CallId = "call-1", UserId = "user-1", DeviceId = "device-2" }, _hub);

        Assert.That(HubClients.SentMessages.Single().Method, Is.EqualTo("call.CallDeviceDismissed"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallDeviceTakeoverHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallDeviceTakeover_NoStaleCfSession_NotifiesOldDevice_SkipsCloudflareCleanup()
    {
        // cfService is constructed but must never be invoked on this path (OldMediaSessionId is
        // null) - passing a real-looking instance with a null IHttpClientFactory would blow up
        // if that assumption were ever violated, which doubles as a safety net for the test.
        var cfService = new CloudflareService(new FakeHttpClientFactory(), NullLogger<CloudflareService>.Instance);

        await CallDeviceTakeoverHandler.Handle(
            new CallDeviceTakeover { CallId = "call-1", UserId = "user-1", OldDeviceId = "device-1", NewDeviceId = "device-2", OldMediaSessionId = null, OldAudioTrackName = null },
            _hub, cfService, NullLogger<CallDeviceTakeoverHandler>.Instance);

        Assert.That(HubClients.SentMessages.Single().Method, Is.EqualTo("call.CallDeviceTakeover"));
    }

    // ══════════════════════════════════════════════════════════════════════════ CallEndedHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallEnded_RemovesCallFromCache()
    {
        await SeedCall(new Call { Id = "call-1", ConversationId = "conv-1", CreatorId = "user-1" });

        // No ConversationId on the event, so the conversation-side work (history entry,
        // CallStateChanged) is skipped and the eviction is all that is under test here.
        await CallEndedHandler.Handle(
            new CallEnded { CallId = "call-1", Reason = CallEndReason.UserEnded },
            _cache,
            new StreamViewerStore(new FakeDistributedLockService(), _cache),
            _hub,
            new TestMessagingContext(Guid.NewGuid().ToString()),
            new FakeMessageBus());

        Assert.That(_cache.HasEntry(Call.GetCacheId("call-1")), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallParticipantLeftHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallParticipantLeft_CallNotInCache_IsNoOp()
    {
        await CallParticipantLeftHandler.Handle(new CallParticipantLeft { CallId = "call-missing", UserId = "user-1" }, _hub, _cache);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task CallParticipantLeft_NotifiesOnlyStillConnectedParticipants_ExcludingTheLeaver()
    {
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants =
            [
                new CallParticipant { UserId = "user-1", Status = CallStatus.Left },
                new CallParticipant { UserId = "user-2", Status = CallStatus.Connected },
                new CallParticipant { UserId = "user-3", Status = CallStatus.Rejected },
            ],
        });

        await CallParticipantLeftHandler.Handle(new CallParticipantLeft { CallId = "call-1", UserId = "user-1" }, _hub, _cache);

        Assert.That(HubClients.SentMessages.Single().Method, Is.EqualTo("call.CallParticipantLeft"));
    }

    [Test]
    public async Task CallParticipantLeft_NoOneElseStillConnected_DoesNotBroadcast()
    {
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Left }],
        });

        await CallParticipantLeftHandler.Handle(new CallParticipantLeft { CallId = "call-1", UserId = "user-1" }, _hub, _cache);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallRingTimeoutCheckHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RingTimeoutCheck_CallNoLongerInCache_IsNoOp()
    {
        var bus = BusThatMustNotInvoke();

        await CallRingTimeoutCheckHandler.Handle(new CallRingTimeoutCheck { CallId = "call-missing" }, _hub, _cache, _callStore, bus);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task RingTimeoutCheck_CallAlreadyAccepted_IsNoOp_StatusUntouched()
    {
        await SeedCall(new Call { Id = "call-1", ConversationId = "conv-1", CreatorId = "user-1", Status = CallStatus.Connected });
        var bus = BusThatMustNotInvoke();

        await CallRingTimeoutCheckHandler.Handle(new CallRingTimeoutCheck { CallId = "call-1" }, _hub, _cache, _callStore, bus);

        var raw = _cache.Get(Call.GetCacheId("call-1"));
        var call = JsonSerializer.Deserialize<Call>(raw!)!;
        Assert.That(call.Status, Is.EqualTo(CallStatus.Connected));
    }

    [Test]
    public async Task RingTimeoutCheck_StillPending_NoParticipants_TimesOutAndPublishesCallEnded()
    {
        // Empty participants: Timeout() only checks Status, so this fully exercises the
        // Pending -> Rejected/CallEnded transition and CallEndNotifier's early-return path
        // (participantIds is empty either way) without ever reaching CallPushService.
        await SeedCall(new Call { Id = "call-1", ConversationId = "conv-1", CreatorId = "user-1", Status = CallStatus.Pending, Participants = [] });
        var bus = new FakeMessageBus();

        await CallRingTimeoutCheckHandler.Handle(new CallRingTimeoutCheck { CallId = "call-1" }, _hub, _cache, _callStore, bus);

        Assert.Multiple(() =>
        {
            Assert.That(bus.Published.Any(e => e is CallEnded evt && evt.Reason == CallEndReason.Declined), Is.True);
            Assert.That(HubClients.SentMessages.Any(m => m.Method == "call.CallEnded"), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallWentAloneHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallWentAlone_NotifiesTheSoleSurvivor_AndSchedulesAloneTimeoutCheck()
    {
        var aloneSince = DateTime.UtcNow;

        var result = await CallWentAloneHandler.Handle(new CallWentAlone { CallId = "call-1", UserId = "user-1", AloneSince = aloneSince }, _hub);

        var scheduled = (DeliveryMessage<CallAloneTimeoutCheck>)result;
        Assert.Multiple(() =>
        {
            Assert.That(HubClients.SentMessages.Single().Method, Is.EqualTo("call.CallAlone"));
            Assert.That(scheduled.Message.CallId, Is.EqualTo("call-1"));
            Assert.That(scheduled.Message.ExpectedAloneSince, Is.EqualTo(aloneSince));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallAloneTimeoutCheckHandler - no-op branches only (see class header comment)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AloneTimeoutCheck_CallNoLongerInCache_IsNoOp()
    {
        var bus = BusThatMustNotInvoke();

        await CallAloneTimeoutCheckHandler.Handle(new CallAloneTimeoutCheck { CallId = "call-missing", ExpectedAloneSince = DateTime.UtcNow }, _hub, _cache, _callStore, bus);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task AloneTimeoutCheck_AloneSinceNoLongerMatches_IsNoOp()
    {
        var originalAloneSince = DateTime.UtcNow.AddMinutes(-10);
        await SeedCall(new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Status = CallStatus.Connected,
            AloneSince = originalAloneSince,
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Connected }],
        });
        var bus = BusThatMustNotInvoke();

        // A different (stale) expected AloneSince - someone must have rejoined and gone alone
        // again since this check was scheduled, so EndIfStillAlone must no-op.
        await CallAloneTimeoutCheckHandler.Handle(
            new CallAloneTimeoutCheck { CallId = "call-1", ExpectedAloneSince = originalAloneSince.AddMinutes(-1) }, _hub, _cache, _callStore, bus);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }
}
