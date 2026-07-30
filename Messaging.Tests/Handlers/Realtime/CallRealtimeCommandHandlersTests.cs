using System.Text.Json;
using Echo.Realtime;
using Messaging.Application.Handler.Realtime;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>
/// Covers the small in-call broadcast handlers (CallCameraHandler, CallMuteHandler,
/// CallScreenShareStartHandler, CallScreenShareStopHandler, CallSpeakingHandler): all share the
/// exact same shape - look the active Call up in the distributed cache by
/// Call.GetCacheId(callId), no-op if it's missing (call already ended / never existed), otherwise
/// broadcast to every OTHER participant over the hub, excluding the acting user themselves.
/// </summary>
[TestFixture]
public class CallRealtimeCommandHandlersTests
{
    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
    }

    private async Task SeedCall(string callId, params string[] participantUserIds)
    {
        var call = new Call
        {
            Id = callId,
            ConversationId = "conv-1",
            CreatorId = participantUserIds.First(),
            Participants = participantUserIds.Select(id => new CallParticipant { UserId = id }).ToList(),
        };
        await _cache.SetAsync(Call.GetCacheId(callId), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
    }

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    // ══════════════════════════════════════════════════════════════════════════
    // CallCameraHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallCamera_CallNotInCache_IsNoOp()
    {
        await CallCameraHandler.Handle(new CallCameraCommand("user-1", "call-missing", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task CallCamera_BroadcastsToOtherParticipants_ExcludingSelf()
    {
        await SeedCall("call-1", "user-1", "user-2", "user-3");

        await CallCameraHandler.Handle(new CallCameraCommand("user-1", "call-1", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(HubClients.SentMessages[0].Method, Is.EqualTo("call.CameraChanged"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallMuteHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallMute_CallNotInCache_IsNoOp()
    {
        await CallMuteHandler.Handle(new CallMuteCommand("user-1", "call-missing", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task CallMute_BroadcastsToOtherParticipants()
    {
        await SeedCall("call-1", "user-1", "user-2");

        await CallMuteHandler.Handle(new CallMuteCommand("user-1", "call-1", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(HubClients.SentMessages[0].Method, Is.EqualTo("call.MuteChanged"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallSpeakingHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallSpeaking_CallNotInCache_IsNoOp()
    {
        await CallSpeakingHandler.Handle(new CallSpeakingCommand("user-1", "call-missing", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task CallSpeaking_BroadcastsToOtherParticipants()
    {
        await SeedCall("call-1", "user-1", "user-2");

        await CallSpeakingHandler.Handle(new CallSpeakingCommand("user-1", "call-1", true), _cache, _hub);

        Assert.That(HubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(HubClients.SentMessages[0].Method, Is.EqualTo("call.SpeakingChanged"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallScreenShareStartHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ScreenShareStart_CallNotInCache_IsNoOp()
    {
        await CallScreenShareStartHandler.Handle(new CallScreenShareStartCommand("user-1", "call-missing", "share-1", "track-1"), _cache, _hub);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task ScreenShareStart_BroadcastsToOtherParticipants_WithSharerCfSessionId()
    {
        var call = new Call
        {
            Id = "call-1",
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Participants =
            [
                new CallParticipant { UserId = "user-1", CfSessionId = "cf-session-abc" },
                new CallParticipant { UserId = "user-2" },
            ],
        };
        await _cache.SetAsync(Call.GetCacheId("call-1"), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());

        await CallScreenShareStartHandler.Handle(new CallScreenShareStartCommand("user-1", "call-1", "share-1", "track-1"), _cache, _hub);

        Assert.That(HubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(HubClients.SentMessages[0].Method, Is.EqualTo("call.ScreenShareStarted"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CallScreenShareStopHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ScreenShareStop_CallNotInCache_IsNoOp()
    {
        await CallScreenShareStopHandler.Handle(new CallScreenShareStopCommand("user-1", "call-missing", "share-1"), _cache, _hub);

        Assert.That(HubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task ScreenShareStop_BroadcastsToOtherParticipants()
    {
        await SeedCall("call-1", "user-1", "user-2");

        await CallScreenShareStopHandler.Handle(new CallScreenShareStopCommand("user-1", "call-1", "share-1"), _cache, _hub);

        Assert.That(HubClients.SentMessages, Has.Count.EqualTo(1));
        Assert.That(HubClients.SentMessages[0].Method, Is.EqualTo("call.ScreenShareStopped"));
    }
}
