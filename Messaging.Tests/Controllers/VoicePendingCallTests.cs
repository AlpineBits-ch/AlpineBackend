using System.Text;
using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers <c>GET voice/call/pending</c>, the catch-up read for an incoming call.
///
/// <para><c>call.IncomingCall</c> is broadcast once over SignalR and never replayed, so a client
/// that was not connected at that moment - opened while the phone was already ringing, or
/// reconnecting after a gap - never learns it is being called; the push fallback is best-effort and
/// can be missed too. This endpoint closes that hole, and the whole risk in it is answering
/// <em>yes</em> when it should not: a stale yes rings a phone for a call that is already over,
/// which is worse than the silence it replaces. Hence the emphasis below on everything that must
/// read as "nothing is ringing".</para>
/// </summary>
[TestFixture]
public class VoicePendingCallTests
{
    private const string CallId = "call-1";
    private const string Caller = "user-caller";
    private const string Callee = "user-callee";

    private FakeDistributedCache _cache = null!;
    private VoiceController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        // IceServerService is never reached by GetPendingCall - it is only touched by the
        // ice-servers endpoint, and constructing one drags in Cloudflare credentials.
        _controller = new VoiceController(
            null!, bus, _cache, callStore,
            new DeviceIdResolver(bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new FakeMessagingHubContext())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(Callee) },
            },
        };
    }

    private async Task Index(string userId, string callId) =>
        await _cache.SetAsync($"user-ringing:{userId}", Encoding.UTF8.GetBytes(callId), new());

    private async Task SeedCall(CallStatus callStatus, CallStatus calleeStatus)
    {
        var call = new Call
        {
            Id = CallId,
            ConversationId = "conv-1",
            CreatorId = Caller,
            Status = callStatus,
            Participants =
            [
                new CallParticipant { UserId = Callee, Status = calleeStatus },
                new CallParticipant { UserId = Caller, Status = CallStatus.Connected },
            ],
        };
        await _cache.SetAsync(
            Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
    }

    [Test]
    public async Task RingingCall_IsReturned()
    {
        await SeedCall(CallStatus.Pending, CallStatus.Pending);
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        var call = (Call)((OkObjectResult)result).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(call.Id, Is.EqualTo(CallId));
            // The client names the ring off this rather than guessing at the roster.
            Assert.That(call.CreatorId, Is.EqualTo(Caller));
        });
    }

    [Test]
    public async Task GroupCallOthersHaveJoined_StillRingsForAnInviteeWhoHasNot()
    {
        // The call as a whole is Connected the moment anyone answers, but this user's own row says
        // whether their phone is still ringing - gating on the call's status alone would go silent
        // for every late joiner in a group call.
        await SeedCall(CallStatus.Connected, CallStatus.Pending);
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task NoIndexEntry_IsNoContent()
    {
        await SeedCall(CallStatus.Pending, CallStatus.Pending);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task IndexPointingAtAnExpiredCall_IsNoContent()
    {
        // The index outlives the call by design - the call's own entry is what expires first when a
        // ring is simply never resolved.
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task AlreadyAnsweredOnAnotherDevice_IsNoContent()
    {
        // The index is never deleted, only allowed to expire, so this is the check that stops a
        // reopened app ringing for a call the user picked up on their laptop a minute ago.
        await SeedCall(CallStatus.Connected, CallStatus.Connected);
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task AlreadyDeclined_IsNoContent()
    {
        await SeedCall(CallStatus.Connected, CallStatus.Rejected);
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task CallAlreadyOver_IsNoContent()
    {
        // Ring timeout, caller hang-up and last-participant-left all land here.
        await SeedCall(CallStatus.Completed, CallStatus.Pending);
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task IndexPointingAtSomebodyElsesCall_IsNoContent()
    {
        // Defensive: whatever put this entry here, a call this user is not part of must never be
        // returned to them.
        var call = new Call
        {
            Id = CallId,
            ConversationId = "conv-1",
            CreatorId = Caller,
            Status = CallStatus.Pending,
            Participants = [new CallParticipant { UserId = "user-stranger", Status = CallStatus.Pending }],
        };
        await _cache.SetAsync(
            Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
        await Index(Callee, CallId);

        var result = await _controller.GetPendingCall();

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }
}
