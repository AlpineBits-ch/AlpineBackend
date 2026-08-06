using System.Text;
using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Realtime.Sfu;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Pins the signalling contract of a 1:1 call: a participant must always end up holding the
/// (cfSessionId, audioTrackName) pair of everyone else who is publishing audio.
/// </summary>
[TestFixture]
public class CallParticipantAnnouncementTests
{
    private const string CallId = "call-1";
    private const string CallerId = "caller-1";
    private const string CalleeId = "callee-1";
    private const string CallerDevice = "caller-device";
    private const string CalleeDevice = "callee-device";
    private const string CallerRustSession = "cf-caller-rust";
    private const string CalleeRustSession = "cf-callee-rust";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private LockedJsonCacheStore _callStore = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);
        // Every device here is a registered one - the unknown-device rejection has its own fixture.
        _bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        // The call exactly as VoiceController.CallAsync leaves it: nobody connected, both Pending.
        _cache.SetEntry(Call.GetCacheId(CallId), JsonSerializer.Serialize(new Call
        {
            Id = CallId,
            ConversationId = "conv-1",
            CreatorId = CallerId,
            Status = CallStatus.Pending,
            Participants =
            [
                new CallParticipant { UserId = CallerId },
                new CallParticipant { UserId = CalleeId },
            ],
        }));
    }

    private CloudflareController ControllerFor(string userId, string deviceId)
    {
        var http = new DefaultHttpContext { User = TestPrincipal.ForUser(userId) };
        http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return new CloudflareController(
            StubCloudflareHttp.CreateService(), _hub, _cache, _callStore, _bus,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            NullLogger<CloudflareController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    /// <summary>The webview's own session: secondary, carries no microphone, connects no device.</summary>
    private Task OpenSecondarySessionAsync(string userId, string deviceId) =>
        ControllerFor(userId, deviceId).CreateSession(CallId, CancellationToken.None, primary: false);

    /// <summary>The audio publisher's session.</summary>
    private async Task OpenPrimarySessionAsync(string userId, string deviceId, string rustSessionId)
    {
        await ControllerFor(userId, deviceId).CreateSession(CallId, CancellationToken.None);
        // The stub Cloudflare always mints the same id, so record ownership of the id this
        // participant will actually publish on by hand.
        _cache.SetEntry($"cf-session-owner:{rustSessionId}", userId);
    }

    private async Task PublishAudioAsync(string userId, string deviceId, string rustSessionId)
    {
        var result = await ControllerFor(userId, deviceId).TracksNew(CallId, new TracksNewBody(
            rustSessionId,
            new CfSessionDescription("offer", "v=0"),
            [new CfTrackNew("local", Mid: "0", TrackName: "audio")]), CancellationToken.None);
        Assert.That(result, Is.InstanceOf<OkObjectResult>(), $"{userId} could not publish audio");
    }

    /// <summary>Everything pushed as <c>call.ParticipantJoined</c>, with its target, serialised
    /// (the payloads are anonymous types from another assembly).</summary>
    private List<(string Target, string Payload)> Announcements() =>
        ((FakeHubClients)_hub.Clients).Sends
            .Where(s => s.Method == "call.ParticipantJoined")
            .Select(s => (s.Target, JsonSerializer.Serialize(s.Args[0])))
            .ToList();

    private List<string> AnnouncementsTo(string userId) =>
        Announcements().Where(a => a.Target == $"user:{userId}").Select(a => a.Payload).ToList();

    private CallParticipant Participant(string userId)
    {
        var raw = _cache.Get(Call.GetCacheId(CallId))!;
        return JsonSerializer.Deserialize<Call>(Encoding.UTF8.GetString(raw))!
            .Participants.Single(p => p.UserId == userId);
    }

    private void Accept(string userId, string deviceId)
    {
        var raw = _cache.Get(Call.GetCacheId(CallId))!;
        var call = JsonSerializer.Deserialize<Call>(Encoding.UTF8.GetString(raw))!;
        call.Accept(userId, deviceId);
        _cache.SetEntry(Call.GetCacheId(CallId), JsonSerializer.Serialize(call));
    }

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CallerWhoseSessionLandsAfterTheCalleePublished_IsStillToldWhatToPull()
    {
        // The reported bug.
        await OpenSecondarySessionAsync(CallerId, CallerDevice);

        // The callee answers quickly and publishes inside that window.
        Accept(CalleeId, CalleeDevice);
        await OpenSecondarySessionAsync(CalleeId, CalleeDevice);
        await OpenPrimarySessionAsync(CalleeId, CalleeDevice, CalleeRustSession);
        await PublishAudioAsync(CalleeId, CalleeDevice, CalleeRustSession);

        Assert.That(AnnouncementsTo(CallerId), Is.Empty,
            "precondition: a Pending participant is deliberately not announced to - the fix is to "
            + "replay it when they connect, not to disclose session ids to someone still ringing");

        // The caller's publisher session finally lands.
        await OpenPrimarySessionAsync(CallerId, CallerDevice, CallerRustSession);

        Assert.That(
            AnnouncementsTo(CallerId).Any(p => p.Contains(CalleeId) && p.Contains(CalleeRustSession)
                                               && p.Contains("\"audioTrackName\":\"audio\"")),
            Is.True,
            "the caller entered the media path after the callee published and was never told which "
            + "session to pull their audio from - nothing else ever repeats that announcement. Sent: "
            + string.Join(" | ", Announcements().Select(a => a.Target + " " + a.Payload)));
    }

    [Test]
    public async Task JoiningTheMediaPath_DoesNotAnnounceTheJoinerToAnybodyElse()
    {
        // A session with no track behind it must never be announced: the subscribe fails, and every
        // client dedupes per user, so the failed attempt burns the guard and the real announcement
        // moments later is discarded as a duplicate. One-way silence for the rest of the call.
        Accept(CalleeId, CalleeDevice);
        await OpenPrimarySessionAsync(CalleeId, CalleeDevice, CalleeRustSession);
        await PublishAudioAsync(CalleeId, CalleeDevice, CalleeRustSession);

        var before = Announcements().Count;
        await OpenPrimarySessionAsync(CallerId, CallerDevice, CallerRustSession);

        Assert.That(Announcements().Skip(before).Select(a => a.Target), Is.All.EqualTo($"user:{CallerId}"),
            "opening a session publishes nothing, so it may only tell the joiner about others");
    }

    [Test]
    public async Task JoiningTheMediaPath_DoesNotNameAParticipantWhoHasOnlyOpenedASession()
    {
        // The callee is Connected and holds a session, but has published no track yet.
        Accept(CalleeId, CalleeDevice);
        await OpenPrimarySessionAsync(CalleeId, CalleeDevice, CalleeRustSession);
        Assert.That(Participant(CalleeId).AudioTrackName, Is.Null, "precondition");

        await OpenPrimarySessionAsync(CallerId, CallerDevice, CallerRustSession);

        Assert.That(AnnouncementsTo(CallerId), Is.Empty,
            "a participant with no AudioTrackName has published nothing and must not be announced "
            + "as pullable; they announce themselves when their own tracks/new lands");
    }

    [Test]
    public async Task CallerWhoPublishesFirst_IsStillToldWhenTheCalleePublishes()
    {
        // The ordering that already worked, and must keep working: the caller wins the race, so the
        // callee's publish is what tells them.
        await OpenSecondarySessionAsync(CallerId, CallerDevice);
        await OpenPrimarySessionAsync(CallerId, CallerDevice, CallerRustSession);
        await PublishAudioAsync(CallerId, CallerDevice, CallerRustSession);

        Accept(CalleeId, CalleeDevice);
        await OpenPrimarySessionAsync(CalleeId, CalleeDevice, CalleeRustSession);
        await PublishAudioAsync(CalleeId, CalleeDevice, CalleeRustSession);

        Assert.Multiple(() =>
        {
            Assert.That(
                AnnouncementsTo(CallerId).Any(p => p.Contains(CalleeId) && p.Contains(CalleeRustSession)),
                Is.True, "the caller was never told about the callee's audio");
            Assert.That(
                AnnouncementsTo(CalleeId).Any(p => p.Contains(CallerId) && p.Contains(CallerRustSession)),
                Is.True, "the callee was never told about the caller's audio");
        });
    }

    [Test]
    public async Task AnnouncementsCarryTheCallId()
    {
        // The engine runs several calls at once, so an announcement that does not say which call it
        // belongs to cannot be routed - guild voice has always carried its channelId.
        await OpenPrimarySessionAsync(CallerId, CallerDevice, CallerRustSession);
        await PublishAudioAsync(CallerId, CallerDevice, CallerRustSession);

        Accept(CalleeId, CalleeDevice);
        await OpenPrimarySessionAsync(CalleeId, CalleeDevice, CalleeRustSession);
        await PublishAudioAsync(CalleeId, CalleeDevice, CalleeRustSession);

        Assert.That(Announcements(), Is.Not.Empty);
        Assert.That(Announcements().Select(a => a.Payload), Is.All.Contains($"\"callId\":\"{CallId}\""));
    }
}
