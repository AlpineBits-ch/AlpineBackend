using System.Text;
using System.Text.Json;
using Echo.Realtime.Caching;
using Messaging.Application.Controllers;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>Covers CloudflareController.CreateSession's <c>primary</c> flag.</summary>
[TestFixture]
public class CloudflareControllerTests
{
    private const string CallId = "call-1";
    private const string UserId = "user-1";
    private const string PrimaryDevice = "desktop-webview";
    private const string PublisherDevice = "desktop-publisher";
    private const string ExistingSessionId = "cf-session-primary";

    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;
    private LockedJsonCacheStore _callStore = null!;
    private CloudflareController _controller = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
        _callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        _controller = new CloudflareController(
            StubCloudflareHttp.CreateService(), new FakeMessagingHubContext(), _cache, _callStore,
            _bus, NullLogger<CloudflareController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(UserId) },
            },
        };

        await SeedConnectedCall();
    }

    /// The user is already connected from their primary device, as they would be before sharing.
    private async Task SeedConnectedCall()
    {
        var call = new Call
        {
            Id = CallId,
            ConversationId = "conv-1",
            CreatorId = UserId,
            Participants =
            [
                new CallParticipant
                {
                    UserId = UserId,
                    Status = CallStatus.Connected,
                    ActiveDeviceId = PrimaryDevice,
                    CfSessionId = ExistingSessionId,
                    AudioTrackName = "audio",
                },
            ],
        };
        await _cache.SetAsync(
            Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
    }

    private void SetDeviceHeader(string deviceId) =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Device-Id"] = deviceId;

    private async Task<CallParticipant> ParticipantAsync()
    {
        var raw = await _cache.GetAsync(Call.GetCacheId(CallId));
        var call = JsonSerializer.Deserialize<Call>(Encoding.UTF8.GetString(raw!));
        return call!.Participants.Single(p => p.UserId == UserId);
    }

    [Test]
    public async Task CreateSession_DefaultsToPrimary_AndConnectsTheDevice()
    {
        // Existing callers pass no flag; their behaviour must be unchanged.
        _cache.Remove(Call.GetCacheId(CallId));
        var call = new Call
        {
            Id = CallId, ConversationId = "conv-1", CreatorId = UserId,
            Participants = [new CallParticipant { UserId = UserId, Status = CallStatus.Pending }],
        };
        await _cache.SetAsync(
            Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
        SetDeviceHeader(PrimaryDevice);

        var result = await _controller.CreateSession(CallId, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var participant = await ParticipantAsync();
        Assert.Multiple(() =>
        {
            Assert.That(participant.Status, Is.EqualTo(CallStatus.Connected));
            Assert.That(participant.ActiveDeviceId, Is.EqualTo(PrimaryDevice));
        });
    }

    [Test]
    public async Task CreateSession_Secondary_FromAnotherDevice_DoesNotTriggerTakeover()
    {
        SetDeviceHeader(PublisherDevice);

        await _controller.CreateSession(CallId, CancellationToken.None, primary: false);

        // A takeover would tell the webview to tear down and end the user's own call.
        Assert.That(_bus.Published.OfType<CallDeviceTakeover>(), Is.Empty);
    }

    [Test]
    public async Task CreateSession_Secondary_LeavesTheParticipantsAudioSessionIntact()
    {
        SetDeviceHeader(PublisherDevice);

        await _controller.CreateSession(CallId, CancellationToken.None, primary: false);

        var participant = await ParticipantAsync();
        Assert.Multiple(() =>
        {
            // ConnectDevice nulls both of these on takeover; the other side needs them to keep
            // hearing this user.
            Assert.That(participant.CfSessionId, Is.EqualTo(ExistingSessionId));
            Assert.That(participant.AudioTrackName, Is.EqualTo("audio"));
            Assert.That(participant.ActiveDeviceId, Is.EqualTo(PrimaryDevice));
        });
    }

    [Test]
    public async Task CreateSession_Secondary_StillReturnsTheNewSessionId()
    {
        SetDeviceHeader(PublisherDevice);

        var result = await _controller.CreateSession(CallId, CancellationToken.None, primary: false);

        // Serialised rather than reflected over: the response is an anonymous type from another
        // assembly.
        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.That(payload, Does.Contain(StubCloudflareHttp.SessionId));
    }

    [Test]
    public async Task CreateSession_Secondary_DoesNotRewriteTheUserToCallMapping()
    {
        SetDeviceHeader(PublisherDevice);

        await _controller.CreateSession(CallId, CancellationToken.None, primary: false);

        // That mapping exists so a disconnect can find the user's call; a screen-publish session
        // disconnecting must not be mistaken for the user leaving.
        Assert.That(_cache.HasEntry($"user-call:{UserId}"), Is.False);
    }
}
