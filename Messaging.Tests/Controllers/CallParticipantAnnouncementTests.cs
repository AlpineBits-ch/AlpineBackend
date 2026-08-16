using Echo.Voice.Transport;
using Echo.Voice.Testing;
using Echo.Voice.Rooms;
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
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Pins the signalling contract of a 1:1 call: a participant must always end up holding the
/// (mediaSessionId, audioTrackName) pair of everyone else who is publishing audio.
/// </summary>
[TestFixture]
public class CallParticipantAnnouncementTests
{
    private const string CallId = "call-1";
    private const string CallerId = "caller-1";
    private const string CalleeId = "callee-1";
    private const string CallerDevice = "caller-device";
    private const string CalleeDevice = "callee-device";
    // The handle the roster records for a publisher, which is now the participant identity at the
    // SFU rather than a minted session id.
    private const string CallerIdentity = CallerId;
    private const string CalleeIdentity = CalleeId;

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

    private CallVoiceMediaController ControllerFor(string userId, string deviceId)
    {
        var http = new DefaultHttpContext { User = TestPrincipal.ForUser(userId) };
        http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return new CallVoiceMediaController(
            new FakeVoiceSfu(), _cache, _callStore, _bus,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            VoiceTestHarness.ServiceFor(_cache, new FakeDistributedLockService(), _hub),
            VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService()),
            NullLogger<CallVoiceMediaController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    /// <summary>The webview's own connection: secondary, carries no microphone, connects no
    /// device.</summary>
    private Task OpenSecondaryConnectionAsync(string userId, string deviceId) =>
        ControllerFor(userId, deviceId).CreateConnection(CallId, CancellationToken.None, primary: false);

    /// <summary>The audio publisher's connection.</summary>
    private Task OpenPrimaryConnectionAsync(string userId, string deviceId) =>
        ControllerFor(userId, deviceId).CreateConnection(CallId, CancellationToken.None);

    private async Task PublishAudioAsync(string userId, string deviceId)
    {
        var result = await ControllerFor(userId, deviceId)
            .Publish(CallId, new CallPublishBody(["audio"]), CancellationToken.None);
        Assert.That(result, Is.InstanceOf<OkObjectResult>(), $"{userId} could not publish audio");
    }

    /// <summary>Everything pushed as <c>call.ParticipantJoined</c>, with its target, serialised
    /// (the payloads are anonymous types from another assembly).</summary>
    private List<(string Target, string Payload)> Announcements() =>
        ((FakeHubClients)_hub.Clients).Sends
            .Where(s => s.Method == "call.ParticipantJoined")
            .Select(s => (s.Target, JsonSerializer.Serialize(s.Args[0])))
            .ToList();

    /// <summary>Announcements this user actually received.</summary>
    private List<string> AnnouncementsTo(string userId) =>
        Announcements()
            .Where(a => a.Target == $"user:{userId}"
                        || (a.Target.StartsWith("users:")
                            && a.Target["users:".Length..].Split(',').Contains(userId)))
            .Select(a => a.Payload)
            .ToList();

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
        await OpenSecondaryConnectionAsync(CallerId, CallerDevice);

        // The callee answers quickly and publishes inside that window.
        Accept(CalleeId, CalleeDevice);
        await OpenSecondaryConnectionAsync(CalleeId, CalleeDevice);
        await OpenPrimaryConnectionAsync(CalleeId, CalleeDevice);
        await PublishAudioAsync(CalleeId, CalleeDevice);

        Assert.That(AnnouncementsTo(CallerId), Is.Empty,
            "precondition: someone still ringing is deliberately not handed session ids");

        // The caller's publisher session finally lands, which is what puts them in the room.
        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);

        // They learn what to pull from the snapshot the join hands back, not from a replayed
        // per-peer event.
        var callee = SnapshotTo(CallerId)!.Participants.Single(p => p.UserId == CalleeId);
        Assert.Multiple(() =>
        {
            Assert.That(callee.MediaSessionId, Is.EqualTo(CalleeIdentity));
            Assert.That(callee.AudioTrackName, Is.EqualTo("audio"));
            Assert.That(callee.PublishState, Is.EqualTo(nameof(VoicePublishState.Publishing)));
        });
    }

    /// <summary>The last snapshot pushed to <paramref name="userId"/>.</summary>
    private VoiceRoomSnapshot? SnapshotTo(string userId) =>
        ((FakeHubClients)_hub.Clients).Sends
            .Where(s => s.Target == $"user:{userId}" && s.Method == "call.Snapshot")
            .Select(s => s.Args[0] as VoiceRoomSnapshot)
            .LastOrDefault();

    [Test]
    public async Task JoiningTheMediaPath_DoesNotAnnounceTheJoinerToAnybodyElse()
    {
        // A session with no track behind it must never be announced: the subscribe fails, and every
        // client dedupes per user, so the failed attempt burns the guard and the real announcement
        // moments later is discarded as a duplicate. One-way silence for the rest of the call.
        Accept(CalleeId, CalleeDevice);
        await OpenPrimaryConnectionAsync(CalleeId, CalleeDevice);
        await PublishAudioAsync(CalleeId, CalleeDevice);

        var before = Announcements().Count;
        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);

        Assert.That(Announcements().Skip(before).Select(a => a.Target), Is.All.EqualTo($"user:{CallerId}"),
            "opening a session publishes nothing, so it may only tell the joiner about others");
    }

    [Test]
    public async Task JoiningTheMediaPath_ClaimsLivenessImmediately()
    {
        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);

        // VoiceHeartbeatCleanupService sweeps both room kinds and evicts anyone with no heartbeat
        // key.
        Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(CallerId)), Is.True);
    }

    [Test]
    public async Task JoiningTheMediaPathWithASecondarySession_ClaimsNoLiveness()
    {
        // A screen-share session is not a presence in the call: it connects no device, joins no
        // roster, and must not create the appearance of a live participant.
        await OpenSecondaryConnectionAsync(CallerId, CallerDevice);

        Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(CallerId)), Is.False);
    }

    [Test]
    public async Task JoiningTheMediaPath_DoesNotNameAParticipantWhoHasOnlyOpenedASession()
    {
        // The callee is Connected and holds a session, but has published no track yet.
        Accept(CalleeId, CalleeDevice);
        await OpenPrimaryConnectionAsync(CalleeId, CalleeDevice);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId));
        Assert.That(room!.Find(CalleeId)!.PublishState, Is.EqualTo(VoicePublishState.Joined),
            "precondition: a session without a track is not publishing");

        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);

        Assert.That(AnnouncementsTo(CallerId), Is.Empty,
            "a participant with no AudioTrackName has published nothing and must not be announced "
            + "as pullable; they announce themselves when their own tracks/new lands");
    }

    [Test]
    public async Task CallerWhoPublishesFirst_IsStillToldWhenTheCalleePublishes()
    {
        // The ordering that already worked, and must keep working: the caller wins the race, so the
        // callee's publish is what tells them.
        await OpenSecondaryConnectionAsync(CallerId, CallerDevice);
        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);
        await PublishAudioAsync(CallerId, CallerDevice);

        Accept(CalleeId, CalleeDevice);
        await OpenPrimaryConnectionAsync(CalleeId, CalleeDevice);
        await PublishAudioAsync(CalleeId, CalleeDevice);

        Assert.Multiple(() =>
        {
            // The caller was already in the room, so the callee's publish reaches them as a live
            // announcement.
            Assert.That(
                AnnouncementsTo(CallerId).Any(p => p.Contains(CalleeId) && p.Contains(CalleeIdentity)),
                Is.True, "the caller was never told about the callee's audio");

            // The callee joined after the caller had already published, so there was no live event
            // left to receive - they learn it from the snapshot their join hands back.
            var caller = SnapshotTo(CalleeId)!.Participants.Single(p => p.UserId == CallerId);
            Assert.That(caller.MediaSessionId, Is.EqualTo(CallerIdentity),
                "the callee was never told about the caller's audio");
            Assert.That(caller.PublishState, Is.EqualTo(nameof(VoicePublishState.Publishing)));
        });
    }

    [Test]
    public async Task AnnouncementsCarryTheCallId()
    {
        // The engine runs several calls at once, so an announcement that does not say which call it
        // belongs to cannot be routed - guild voice has always carried its channelId.
        await OpenPrimaryConnectionAsync(CallerId, CallerDevice);
        await PublishAudioAsync(CallerId, CallerDevice);

        Accept(CalleeId, CalleeDevice);
        await OpenPrimaryConnectionAsync(CalleeId, CalleeDevice);
        await PublishAudioAsync(CalleeId, CalleeDevice);

        Assert.That(Announcements(), Is.Not.Empty);
        Assert.That(Announcements().Select(a => a.Payload), Is.All.Contains($"\"callId\":\"{CallId}\""));
    }
}
