using Echo.Voice.Testing;
using System.Text;
using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Application.Handler.Realtime;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Messaging.Application.Dtos.Response;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers <c>GET voice/call/active</c>, the launch read behind the reconnect banner.
/// </summary>
[TestFixture]
public class VoiceActiveCallTests
{
    private const string CallId = "call-1";
    private const string Peer = "user-peer";
    private const string Me = "user-me";

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

        _controller = new VoiceController(
            bus, _cache, callStore,
            new DeviceIdResolver(bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            TestPrivacyServices.Build(bus).Policy,
            new StreamViewerStore(new FakeDistributedLockService(), _cache),
            new TestMessagingContext(Guid.NewGuid().ToString()),
            VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService()),
            VoiceTestHarness.ServiceFor(_cache, new FakeDistributedLockService(), new FakeMessagingHubContext()),
            new FakeMessagingHubContext())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(Me) },
            },
        };
    }

    /// <summary>The trail the disconnect handler leaves as it hangs the user up.</summary>
    private Task RecentIndex(string callId) =>
        _cache.SetAsync(UserDisconnectedHandler.RecentCallKey(Me), Encoding.UTF8.GetBytes(callId), new());

    /// <summary>The forward index, which survives only when the disconnect never arrived at all - a
    /// power cut, a killed container - so nothing ever ran to clear it.</summary>
    private Task LiveIndex(string callId) =>
        _cache.SetAsync($"user-call:{Me}", Encoding.UTF8.GetBytes(callId), new());

    private Task SeedCall(CallStatus callStatus, CallStatus myStatus, CallStatus peerStatus) =>
        _cache.SetAsync(
            Call.GetCacheId(CallId),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Call
            {
                Id = CallId,
                ConversationId = "conv-1",
                CreatorId = Peer,
                Status = callStatus,
                Participants =
                [
                    new CallParticipant { UserId = Me, Status = myStatus },
                    new CallParticipant { UserId = Peer, Status = peerStatus },
                ],
            })),
            new());

    [Test]
    public async Task ACallStillRunningWithoutUs_IsOffered()
    {
        await SeedCall(CallStatus.Connected, CallStatus.Left, CallStatus.Connected);
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        var call = (OngoingCallDto)((OkObjectResult)result).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(call.CallId, Is.EqualTo(CallId));
            Assert.That(call.ConnectedUserIds, Is.EqualTo(new[] { Peer }),
                "the banner names who is still in there, so the offer is not a leap of faith");
        });
    }

    [Test]
    public async Task TheForwardIndexIsAlsoAccepted_ForADropThatNeverReportedItself()
    {
        // A power cut leaves no disconnect behind at all: user-call: is still there because nothing
        // ran to clear it, and the participant is still Connected in a call nobody hung up.
        await SeedCall(CallStatus.Connected, CallStatus.Left, CallStatus.Connected);
        await LiveIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task NoIndexAtAll_IsNoContent()
    {
        await SeedCall(CallStatus.Connected, CallStatus.Left, CallStatus.Connected);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task IndexPointingAtAnExpiredCall_IsNoContent()
    {
        // The trail outlives the call: it is absolute and deliberately never deleted, because a call
        // ends in half a dozen ways and each of them would have to remember.
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task ACallThatHasSinceEnded_IsNoContent()
    {
        await SeedCall(CallStatus.Completed, CallStatus.Left, CallStatus.Left);
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task AlreadyBackOnTheCallFromAnotherDevice_IsNoContent()
    {
        // The desktop was restarting while the user answered on their phone.
        await SeedCall(CallStatus.Connected, CallStatus.Connected, CallStatus.Connected);
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task ACallNobodyIsLeftIn_IsNoContent()
    {
        // Inside its alone-timeout and about to end. Rejoining is an offer to sit in an empty room.
        await SeedCall(CallStatus.Connected, CallStatus.Left, CallStatus.Left);
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task IndexPointingAtSomebodyElsesCall_IsNoContent()
    {
        await _cache.SetAsync(
            Call.GetCacheId(CallId),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Call
            {
                Id = CallId,
                ConversationId = "conv-1",
                CreatorId = Peer,
                Status = CallStatus.Connected,
                Participants = [new CallParticipant { UserId = Peer, Status = CallStatus.Connected }],
            })),
            new());
        await RecentIndex(CallId);

        var result = await _controller.GetActiveCall(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }
}
