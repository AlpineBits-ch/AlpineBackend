using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Transport;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>
/// The channel side of <c>POST .../alive</c>: the liveness assertion the desktop client's media
/// process makes, from outside the webview the OS is allowed to freeze.
/// </summary>
[TestFixture]
public class GuildVoiceLivenessTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string ParticipantId = "user-participant";
    private const string OutsiderId = "user-outsider";
    private const string LiveDevice = "device-live";
    private const string SupersededDevice = "device-superseded";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();

        // No guild rows are seeded, and that is the point: this route consults the roster and
        // nothing else.
        await VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = ChannelId,
            Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
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

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private GuildVoiceController ControllerFor(string userId, string deviceId)
    {
        var locks = new FakeDistributedLockService();
        var http = new DefaultHttpContext { User = TestPrincipal.Create(userId) };
        http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;

        return new GuildVoiceController(
            new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance),
            _hub, _cache,
            new CloudflareMediaTransport(StubCloudflareHttp.CreateService()), _context,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new GuildVoiceActivityStore(locks, _cache),
            new StreamViewerStore(locks, _cache),
            VoiceTestHarness.ServiceFor(_cache, locks, _hub),
            VoiceTestHarness.StoreFor(_cache, locks),
            VoiceRingTestFactory.Create(_context, _cache, locks, _hub, _bus), _bus)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private Task<IActionResult> AliveAsync(string userId, string deviceId) =>
        ControllerFor(userId, deviceId).Alive(GuildId, ChannelId, CancellationToken.None);

    /// <summary>The expiry currently recorded against the caller's liveness key, or null if there is
    /// no key.</summary>
    private TimeSpan? LivenessTtlOf(string userId) =>
        _cache.OptionsFor(VoiceReconciler.LivenessKey(userId))?.AbsoluteExpirationRelativeToNow;

    /// <summary>Shortens the liveness key exactly the way <c>GuildLifecycleHandler</c> does when the
    /// socket carrying this participant's voice connection drops.</summary>
    private Task OpenDisconnectGraceAsync(string userId) =>
        _cache.SetStringAsync(
            VoiceReconciler.LivenessKey(userId), VoiceRoomKey.Channel(ChannelId).ToString(),
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

    [Test]
    public async Task Alive_FromASupersededDevice_Is409AndLeavesTheLiveDevicesGraceAlone()
    {
        // The old device of a takeover: same user, still running, no longer the one in the channel.
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
        var before = (await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId)))!;

        await AliveAsync(ParticipantId, LiveDevice);

        var after = (await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId)))!;

        Assert.Multiple(() =>
        {
            // A bumped version is not harmless here: every client watching this channel would conclude
            // it had missed an event and refetch a snapshot, twice a minute per participant, forever.
            Assert.That(after.Version, Is.EqualTo(before.Version),
                "asserting liveness is not a change to the room");
            Assert.That(after.InstanceId, Is.EqualTo(before.InstanceId));
            Assert.That(JsonSerializer.Serialize(after.Participants),
                Is.EqualTo(JsonSerializer.Serialize(before.Participants)));
            Assert.That(((FakeHubClients)_hub.Clients).SentMessages, Is.Empty,
                "and there is nothing for anybody else to be told - no UserLeftVoice, no Resync");
        });
    }
}
