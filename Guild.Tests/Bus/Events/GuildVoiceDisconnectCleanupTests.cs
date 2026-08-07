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
/// Covers the two defects in <see cref="GuildLifecycleHandler"/>'s <c>UserDisconnected</c> voice
/// cleanup that made guild voice participants vanish from each other's rosters:
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
        CfSessionId = "cf-session",
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
            VoiceTestHarness.StoreFor(effectiveCache, locks ?? new FakeDistributedLockService()), _hub, _bus,
            blocks);
    }

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

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_StillRemovesTheParticipant()
    {
        // The genuine case, which the device guard must not break.
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        _cache.SetEntry(VoiceReconciler.LivenessKey(UserId), "1");

        await HandleDisconnectAsync(UserId, DesktopDevice);

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Not.Contain(UserId));
            Assert.That(HubMessages().Select(m => m.Method), Does.Contain("guild.voice.UserLeftVoice"));
            Assert.That(_cache.HasEntry(VoiceReconciler.LivenessKey(UserId)), Is.False);
        });
    }

    [Test]
    public async Task Disconnect_WithNoDeviceIdOnEitherSide_FallsBackToRemovingTheParticipant()
    {
        // Pre-device-tracking clients and roster entries written before DeviceId existed can't be
        // attributed to a device.
        SeedVoice(UserId, Participant(UserId, deviceId: null));

        await HandleDisconnectAsync(UserId, deviceId: null);

        Assert.That(await RosterAsync(), Does.Not.Contain(UserId));
    }

    // ══════════════════════════════════════════════════════════════════════════ 2.

    [Test]
    public async Task Disconnect_RacingAJoin_DoesNotEraseTheJoinerFromTheRoster()
    {
        const string joiner = "user-2";
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        var channelKey = VoiceRoomKey.Channel(ChannelId).CacheKey;
        var locks = new TrackingDistributedLockService();

        // A second request commits an unrelated user's join at the worst possible moment: right
        // after the handler has read the channel blob.
        Action? deferredJoin = null;
        var cache = new InterceptingDistributedCache(_cache, channelKey, () =>
        {
            void CommitJoin()
            {
                var raw = _cache.Get(channelKey);
                var state = JsonSerializer.Deserialize<VoiceRoom>(
                    System.Text.Encoding.UTF8.GetString(raw!))!;
                state.Participants.Add(Participant(joiner, "joiner-device"));
                _cache.SetEntry(channelKey, JsonSerializer.Serialize(state));
            }

            if (locks.IsHeld(channelKey)) deferredJoin = CommitJoin;
            else CommitJoin();
        });

        await HandleDisconnectAsync(
            UserId, DesktopDevice, cache, locks);
        deferredJoin?.Invoke();

        // Unserialised, the handler's stale copy won: user-2 got a 200 from POST /join with
        // themselves in the returned roster, the server kept no record of them, nobody was told
        // they joined, and ExchangeParticipantJoined announced nothing in either direction.
        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Contain(joiner),
                "a disconnect must not last-writer-wins over a join that committed while it was "
                + "holding a stale copy of the channel blob");
            Assert.That(roster, Does.Not.Contain(UserId),
                "and the disconnect's own removal must still stick - both writes survive");
        });
    }

    [Test]
    public async Task Disconnect_WritesTheChannelBlobWhileHoldingTheChannelLock()
    {
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        var channelKey = VoiceRoomKey.Channel(ChannelId).CacheKey;
        var timeline = new List<string>();
        var locks = new TrackingDistributedLockService(timeline);
        var cache = new RecordingDistributedCache(_cache, timeline);

        await HandleDisconnectAsync(UserId, DesktopDevice, cache, locks);

        var acquired = timeline.IndexOf($"acquired:{channelKey}");
        var written = timeline.IndexOf($"set:{channelKey}");
        var released = timeline.IndexOf($"released:{channelKey}");

        Assert.Multiple(() =>
        {
            Assert.That(acquired, Is.GreaterThanOrEqualTo(0), "the channel lock was never taken");
            Assert.That(written, Is.GreaterThan(acquired), "the roster was written before the lock was held");
            Assert.That(released, Is.GreaterThan(written), "the lock was released before the roster was written");
        });
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Grants every lock immediately (these tests are single-threaded) but tracks which
    /// keys are currently held, and optionally records acquire/release into a shared timeline.</summary>
    private sealed class TrackingDistributedLockService : IDistributedLockService
    {
        private readonly HashSet<string> _held = [];
        private readonly List<string>? _timeline;

        public TrackingDistributedLockService(List<string>? timeline = null) => _timeline = timeline;

        public bool IsHeld(string key) => _held.Contains(key);

        public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan? wait = null, CancellationToken ct = default)
        {
            _held.Add(key);
            _timeline?.Add($"acquired:{key}");
            return Task.FromResult<IAsyncDisposable>(new Release(this, key));
        }

        private sealed class Release(TrackingDistributedLockService owner, string key) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner._held.Remove(key);
                owner._timeline?.Add($"released:{key}");
                return ValueTask.CompletedTask;
            }
        }
    }

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

    /// <summary>
    /// <see cref="IDistributedCache"/> decorator that fires <c>onFirstRead</c> immediately after
    /// the first read of <c>watchedKey</c> returns, and hands the caller the value read before that
    /// callback ran.
    /// </summary>
    private sealed class InterceptingDistributedCache(
        IDistributedCache inner, string watchedKey, Action onFirstRead) : IDistributedCache
    {
        private bool _fired;

        private byte[]? Intercept(string key, byte[]? value)
        {
            if (_fired || key != watchedKey) return value;
            _fired = true;
            onFirstRead();
            return value;
        }

        public byte[]? Get(string key) => Intercept(key, inner.Get(key));

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Intercept(key, await inner.GetAsync(key, token));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            inner.SetAsync(key, value, options, token);

        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
