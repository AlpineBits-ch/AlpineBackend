using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Testing;
using Echo.Voice.Transport;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// The call side of <c>POST .../alive</c>: the liveness assertion the desktop client's media
/// process makes, from outside the webview the OS is allowed to freeze.
/// </summary>
[TestFixture]
public class CallVoiceLivenessTests
{
    private const string CallId = "call-1";
    private const string ParticipantId = "user-participant";
    private const string OutsiderId = "user-outsider";
    private const string LiveDevice = "device-live";
    private const string SupersededDevice = "device-superseded";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        // Every device here is a registered one - the unknown-device rejection has its own fixture.
        _bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        // Seeded rather than built through a join, so that the roster's version is a fixed number
        // the assertions below can pin.
        await VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = CallId,
            Kind = VoiceRoomKind.Call,
            Participants =
            [
                new VoiceParticipant
                {
                    UserId = ParticipantId, DeviceId = LiveDevice,
                    MediaSessionId = "cf-participant", AudioTrackName = "audio",
                },
            ],
        });
    }

    /// <param name="deviceId">Null sends no <c>X-Device-Id</c> at all, which is a different thing
    /// from sending the shared default bucket and is exactly the distinction the route has to
    /// make.</param>
    private CallVoiceMediaController ControllerFor(string userId, string? deviceId)
    {
        var http = new DefaultHttpContext { User = TestPrincipal.ForUser(userId) };
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        var locks = new FakeDistributedLockService();

        return new CallVoiceMediaController(
            new CloudflareMediaTransport(StubCloudflareHttp.CreateService()), _cache,
            new LockedJsonCacheStore(locks, _cache), _bus,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new SfuSessionOwnership(_cache),
            VoiceTestHarness.ServiceFor(_cache, locks, _hub),
            VoiceTestHarness.StoreFor(_cache, locks),
            NullLogger<CallVoiceMediaController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private Task<IActionResult> AliveAsync(string userId, string? deviceId) =>
        ControllerFor(userId, deviceId).Alive(CallId, CancellationToken.None);

    /// <summary>The expiry currently recorded against the caller's liveness key, or null if there is
    /// no key.</summary>
    private TimeSpan? LivenessTtlOf(string userId) =>
        _cache.OptionsFor(VoiceReconciler.LivenessKey(userId))?.AbsoluteExpirationRelativeToNow;

    /// <summary>Shortens the liveness key exactly the way <c>UserDisconnectedHandler</c> does when the
    /// socket carrying this participant's voice connection drops.</summary>
    private Task OpenDisconnectGraceAsync(string userId) =>
        _cache.SetStringAsync(
            VoiceReconciler.LivenessKey(userId), VoiceRoomKey.Call(CallId).ToString(),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = VoiceReconciler.DisconnectGraceTtl,
            });

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Alive_FromTheDeviceOnTheRoster_RestoresTheFullLivenessTtl()
    {
        var result = await AliveAsync(ParticipantId, LiveDevice);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContentResult>(),
                "the route asserts a fact and returns no body - there is nothing for the media "
                + "process to parse");
            Assert.That(_cache.Get(VoiceReconciler.LivenessKey(ParticipantId)), Is.Not.Null);
            Assert.That(LivenessTtlOf(ParticipantId), Is.EqualTo(VoiceReconciler.LivenessTtl),
                "anything shorter and the sweep still takes them, just later");
        });
    }

    [Test]
    public async Task Alive_CancelsADisconnectGraceThatHadAlreadyShortenedTheKey()
    {
        // This is the case the whole route exists for.
        await OpenDisconnectGraceAsync(ParticipantId);
        Assert.That(LivenessTtlOf(ParticipantId), Is.EqualTo(VoiceReconciler.DisconnectGraceTtl),
            "precondition: the disconnect grace is open");

        await AliveAsync(ParticipantId, LiveDevice);

        Assert.That(LivenessTtlOf(ParticipantId), Is.EqualTo(VoiceReconciler.LivenessTtl),
            "the write replaces the expiry rather than adding to it, which is what cancels the grace");
    }

    [Test]
    public async Task Alive_FromSomebodyWhoIsNotOnTheRoster_Is404AndWritesNothing()
    {
        var result = await AliveAsync(OutsiderId, LiveDevice);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFoundResult>());
            Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(OutsiderId)), Is.False,
                "a key for somebody the roster does not have would make the sweep spare a "
                + "participant who does not exist");
        });
    }

    /// <summary>
    /// A caller that names no device at all is trusted, which is what <c>IsVoiceDevice</c> has
    /// always claimed and what the route did not do.
    /// </summary>
    [Test]
    public async Task Alive_FromAClientThatSendsNoDeviceHeaderAtAll_IsAccepted()
    {
        var result = await AliveAsync(ParticipantId, null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContentResult>());
            Assert.That(LivenessTtlOf(ParticipantId), Is.EqualTo(VoiceReconciler.LivenessTtl));
        });
    }

    /// <summary>The other half of the pair: a client that <em>does</em> name a device is still held
    /// to it, so the tolerance above cannot be reached by sending the bucket name on purpose.</summary>
    [Test]
    public async Task Alive_FromAClientThatNamesTheDefaultBucketWhileTheRosterHasARealDevice_Is409()
    {
        var result = await AliveAsync(ParticipantId, DeviceIdentity.DefaultDeviceId);

        Assert.That(result, Is.InstanceOf<ConflictResult>());
    }

    [Test]
    public async Task Alive_FromASupersededDevice_Is409AndLeavesTheLiveDevicesGraceAlone()
    {
        // The old device of a takeover: same user, still running, no longer the one in the call.
        await OpenDisconnectGraceAsync(ParticipantId);

        var result = await AliveAsync(ParticipantId, SupersededDevice);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ConflictResult>());
            Assert.That(LivenessTtlOf(ParticipantId), Is.EqualTo(VoiceReconciler.DisconnectGraceTtl),
                "the grace opened against the live device must run its course untouched");
        });
    }

    [Test]
    public async Task Alive_TouchesNeitherTheRosterNorTheVersion_AndAnnouncesNothing()
    {
        var before = (await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId)))!;

        await AliveAsync(ParticipantId, LiveDevice);

        var after = (await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId)))!;

        Assert.Multiple(() =>
        {
            // A bumped version is not harmless here: every client watching this room would conclude
            // it had missed an event and refetch a snapshot, twice a minute per participant, forever.
            Assert.That(after.Version, Is.EqualTo(before.Version),
                "asserting liveness is not a change to the room");
            Assert.That(after.InstanceId, Is.EqualTo(before.InstanceId));
            Assert.That(JsonSerializer.Serialize(after.Participants),
                Is.EqualTo(JsonSerializer.Serialize(before.Participants)));
            Assert.That(((FakeHubClients)_hub.Clients).Sends, Is.Empty,
                "and there is nothing for anybody else to be told");
        });
    }
}
