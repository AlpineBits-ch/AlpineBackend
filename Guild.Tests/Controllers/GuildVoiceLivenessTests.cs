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

    /// <param name="deviceId">Null sends no <c>X-Device-Id</c> at all, which is a different thing
    /// from sending the shared default bucket and is exactly the distinction the route has to
    /// make.</param>
    private GuildVoiceController ControllerFor(string userId, string? deviceId)
    {
        var locks = new FakeDistributedLockService();
        var http = new DefaultHttpContext { User = TestPrincipal.Create(userId) };
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;

        return new GuildVoiceController(
            PermissionTestFactory.Create(_cache, _context),
            _hub, _cache,
            _context,
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

    private Task<IActionResult> AliveAsync(string userId, string? deviceId) =>
        ControllerFor(userId, deviceId).Alive(GuildId, ChannelId, CancellationToken.None);

    /// <summary>The device the roster holds this channel's audio on, which is the one every claim is
    /// keyed under - see <c>VoiceReconciler.LivenessKey</c>.</summary>
    private static string KeyFor(string userId, string device = LiveDevice) =>
        VoiceReconciler.LivenessKey(userId, device);

    /// <summary>The expiry currently recorded against the caller's liveness key, or null if there is
    /// no key.</summary>
    private TimeSpan? LivenessTtlOf(string userId) =>
        _cache.OptionsFor(KeyFor(userId))?.AbsoluteExpirationRelativeToNow;

    /// <summary>Shortens the liveness key exactly the way <c>GuildLifecycleHandler</c> does when the
    /// socket carrying this participant's voice connection drops.</summary>
    private Task OpenDisconnectGraceAsync(string userId) =>
        _cache.SetStringAsync(
            KeyFor(userId), VoiceRoomKey.Channel(ChannelId).ToString(),
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
            Assert.That(_cache.Get(KeyFor(ParticipantId)), Is.Not.Null);
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
            Assert.That(_cache.HasEntry(KeyFor(OutsiderId)), Is.False,
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
