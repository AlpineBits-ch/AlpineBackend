using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Covers the defects in <see cref="GuildLifecycleHandler"/>'s connection-lifecycle voice handling
/// that made guild voice participants vanish from each other's rosters:
/// </summary>
[TestFixture]
public class GuildVoiceDisconnectCleanupTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string UserId = "user-1";
    private const string DesktopDevice = "desktop-1";
    private const string PhoneDevice = "phone-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildHydrateService _hydrate = null!;
    private GuildLifecycleHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _hydrate = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _handler = new GuildLifecycleHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>Puts <paramref name="participants"/> in the channel and points the user's
    /// voice-location key at it, which is what the disconnect handler keys off.</summary>
    private void SeedVoice(string locationUserId, params VoiceParticipant[] participants)
    {
        var room = new VoiceRoom
        {
            RoomId = ChannelId,
            Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants = participants.ToList(),
        };
        _cache.SetEntry(room.Key.CacheKey, JsonSerializer.Serialize(room));
        _cache.SetEntry(
            ChannelVoiceState.GetUserCacheKey(locationUserId),
            JsonSerializer.Serialize(new { ChannelId, GuildId, DeviceId = DesktopDevice }));
    }

    private static VoiceParticipant Participant(string userId, string? deviceId) => new()
    {
        UserId = userId,


        DeviceId = deviceId,
        MediaSessionId = "cf-session",
        AudioTrackName = "audio",
    };

    private async Task<List<string>> RosterAsync()
    {
        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        return room!.Participants.Select(p => p.UserId).ToList();
    }

    private List<(string Method, object?[] Args)> HubMessages() =>
        ((FakeHubClients)_hub.Clients).SentMessages;

    private Task HandleDisconnectAsync(
        string userId, string? deviceId, IDistributedCache? cache = null, IDistributedLockService? locks = null)
    {
        var effectiveCache = cache ?? _cache;
        // No blocks: this suite is about voice cleanup, and the block filter only ever removes
        // presence recipients - of which the fake Redis reports none anyway.
        var blocks = PrivacyTestFactory.Blocks(new FakeInvokingMessageBus(), new FakeDistributedCache());
        return _handler.Handle(
            new UserDisconnected(userId, deviceId), _context, _hydrate, effectiveCache,
            VoiceTestHarness.StoreFor(effectiveCache, locks ?? new FakeDistributedLockService()), _hub,
            blocks);
    }

    private Task HandleConnectAsync(string userId, string? deviceId)
    {
        var privacyBus = new FakeInvokingMessageBus();
        var privacyCache = new FakeDistributedCache();
        return _handler.Handle(
            new UserConnected(userId, deviceId), _context, _hydrate, _hub,
            PrivacyTestFactory.Blocks(privacyBus, privacyCache),
            PrivacyTestFactory.Privacy(privacyBus, privacyCache, PrivacyTestFactory.Permissive(userId)),
            _cache, VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService()));
    }

    /// <summary>The lifetime the last write to the heartbeat key asked for.</summary>
    private TimeSpan? HeartbeatTtl() =>
        _cache.OptionsFor(VoiceReconciler.LivenessKey(UserId))?.AbsoluteExpirationRelativeToNow;

    // ══════════════════════════════════════════════════════════════════════════ 1.

    [Test]
    public async Task Disconnect_FromADeviceThatIsNotInVoice_LeavesTheParticipantInTheChannel()
    {
        // The user is talking on desktop.
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        await HandleDisconnectAsync(UserId, PhoneDevice);

        // Removing them here left the desktop client connected, publishing and subscribed while the
        // server's roster forgot about it: every other member rendered the channel as empty and the
        // user sat in it talking to nobody.
        Assert.That(await RosterAsync(), Does.Contain(UserId),
            "a socket drop on a device that does not hold the voice connection must not remove the "
            + "participant - VoiceState.DeviceId records which device does");
    }

    [Test]
    public async Task Disconnect_FromADeviceThatIsNotInVoice_DoesNotBroadcastUserLeftVoice()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        await HandleDisconnectAsync(UserId, PhoneDevice);

        Assert.That(HubMessages().Select(m => m.Method), Does.Not.Contain("guild.voice.UserLeftVoice"),
            "telling the guild the user left is what made them disappear from everyone else's "
            + "channel list while their real client was still in the channel");
    }

    [Test]
    public async Task Disconnect_FromADeviceThatIsNotInVoice_KeepsTheHeartbeatKeyAlive()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        _cache.SetEntry(VoiceReconciler.LivenessKey(UserId), "1");

        await HandleDisconnectAsync(UserId, PhoneDevice);

        // Dropping the heartbeat key is a second, delayed kill: even with the roster entry intact,
        // VoiceHeartbeatCleanupService evicts anyone without one on its next 60s sweep.
        Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(UserId)), Is.True,
            "the heartbeat belongs to the device that is in voice, not to the one that disconnected");
    }

    // ══════════════════════════════════════════════════════════════════════════ 2.

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_LeavesTheParticipantInTheChannel()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        _cache.SetEntry(VoiceReconciler.LivenessKey(UserId), "1");

        await HandleDisconnectAsync(UserId, DesktopDevice);

        // This is the reported bug: a socket closing is not a departure.
        Assert.That(await RosterAsync(), Does.Contain(UserId),
            "a disconnect opens a grace window; only the sweep may remove a participant");
    }

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_DoesNotAnnounceThemGone()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        await HandleDisconnectAsync(UserId, DesktopDevice);

        // The announcement is the sweep's to make, once the grace window has actually elapsed.
        Assert.That(HubMessages().Select(m => m.Method), Does.Not.Contain("guild.voice.UserLeftVoice"));
    }

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_ShortensTheHeartbeatKeyToTheGrace()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        await HandleDisconnectAsync(UserId, DesktopDevice);

        // Not removed, and not left at the full 90s either: the key is what the sweep reads, so
        // shortening it is how a disconnect still leads to an eviction - just not instantly.
        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(UserId)), Is.True);
            Assert.That(HeartbeatTtl(), Is.EqualTo(VoiceReconciler.DisconnectGraceTtl));
        });
    }

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_KeepsTheVoiceLocationPointer()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        await HandleDisconnectAsync(UserId, DesktopDevice);

        // The pointer is how a reconnect finds the room to restore liveness for.
        Assert.That(_cache.HasEntry(ChannelVoiceState.GetUserCacheKey(UserId)), Is.True);
    }

    [Test]
    public async Task Disconnect_WithNoDeviceIdOnEitherSide_StillOnlyOpensTheGraceWindow()
    {
        // Pre-device-tracking clients and roster entries written before DeviceId existed can't be
        // attributed to a device, so the guard falls through to treating the drop as theirs.
        SeedVoice(UserId, Participant(UserId, deviceId: null));

        await HandleDisconnectAsync(UserId, deviceId: null);

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Contain(UserId));
            Assert.That(HeartbeatTtl(), Is.EqualTo(VoiceReconciler.DisconnectGraceTtl));
        });
    }

    [Test]
    public async Task Disconnect_WithTheRoomAlreadyGone_DropsTheDanglingKeys()
    {
        // No roster to preserve, so there is nothing for a grace window to protect: the room blob
        // expired or was already swept.
        _cache.SetEntry(
            ChannelVoiceState.GetUserCacheKey(UserId),
            JsonSerializer.Serialize(new { ChannelId, GuildId, DeviceId = DesktopDevice }));
        _cache.SetEntry(VoiceReconciler.LivenessKey(UserId), "1");

        await HandleDisconnectAsync(UserId, DesktopDevice);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(ChannelVoiceState.GetUserCacheKey(UserId)), Is.False);
            Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(UserId)), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ 3.

    [Test]
    public async Task Reconnect_FromTheDeviceInVoice_RestoresTheFullHeartbeatTtl()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        await HandleDisconnectAsync(UserId, DesktopDevice);

        await HandleConnectAsync(UserId, DesktopDevice);

        // Without this, surviving a reconnect depends on the client's next heartbeat landing inside
        // the window - a coin toss on a 30s timer. The connect is the earlier and stronger signal.
        Assert.That(HeartbeatTtl(), Is.EqualTo(VoiceReconciler.LivenessTtl));
    }

    [Test]
    public async Task Reconnect_FromADifferentDevice_DoesNotRestoreTheWindow()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        await HandleDisconnectAsync(UserId, DesktopDevice);

        await HandleConnectAsync(UserId, PhoneDevice);

        // The phone coming online says nothing about the desktop that was talking.
        Assert.That(HeartbeatTtl(), Is.EqualTo(VoiceReconciler.DisconnectGraceTtl));
    }

    [Test]
    public async Task Connect_WithNoVoiceLocation_WritesNoHeartbeatKey()
    {
        await HandleConnectAsync(UserId, DesktopDevice);

        // Someone who is not in voice must not acquire liveness by connecting: the sweep would then
        // find a heartbeat for a user it has no roster entry for, and the key would outlive nothing.
        Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(UserId)), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ 4.

    [Test]
    public async Task Disconnect_NeverWritesTheRoomBlob()
    {
        const string joiner = "user-2";
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        var channelKey = VoiceRoomKey.Channel(ChannelId).CacheKey;
        var timeline = new List<string>();
        var cache = new RecordingDistributedCache(_cache, timeline);

        await HandleDisconnectAsync(UserId, DesktopDevice, cache);

        // A read-modify-write on the shared channel blob is how a disconnect used to erase an
        // unrelated participant who joined during its window: it last-writer-wins over anything
        // that committed while it held a stale copy, so the joiner got a 200 from POST /join with
        // themselves in the returned roster and the server kept no record of them.
        Assert.That(timeline, Does.Not.Contain($"set:{channelKey}"));

        // And a concurrent join is therefore untouched, whenever it lands.
        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        room!.Participants.Add(Participant(joiner, "joiner-device"));
        _cache.SetEntry(channelKey, JsonSerializer.Serialize(room));

        Assert.That(await RosterAsync(), Does.Contain(joiner));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Records every write into a shared timeline, so a write can be ordered against the
    /// lock acquire/release around it.</summary>
    private sealed class RecordingDistributedCache(IDistributedCache inner, List<string> timeline) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            timeline.Add($"set:{key}");
            inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            timeline.Add($"set:{key}");
            return inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
