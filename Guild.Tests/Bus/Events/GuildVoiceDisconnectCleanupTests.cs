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
///
/// <list type="number">
/// <item><b>It ignored the disconnecting device.</b> <see cref="VoiceState.DeviceId"/> records which
/// device actually holds the voice connection (that is what
/// <c>GuildVoiceController.TakeoverDeviceAsync</c> maintains it for), but the handler removed the
/// participant on a socket drop from <em>any</em> of the user's devices. Messaging's equivalent
/// handler already guarded this - <c>UserDisconnectedHandler</c> routes through
/// <c>Call.Leave(userId, deviceId)</c> precisely so "the old device's own disconnect can't stomp on
/// a different device's active call". Guild never got the same fix.</item>
///
/// <item><b>It was an unlocked read-modify-write on the shared channel blob.</b> Every other writer
/// of <c>voice:channel:{id}</c> (Join, CreateSession, ExchangeParticipantJoined, CloseTracks, the
/// mute/deafen/screenshare handlers, the heartbeat sweeper) had been moved onto
/// <c>LockedJsonCacheStore</c>; this call site still loaded, mutated and saved the whole blob with
/// no lock, so it last-writer-wins over anything that committed in between - erasing an unrelated
/// participant who joined during the window.</item>
/// </list>
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
    private void SeedVoice(string locationUserId, params VoiceState[] participants)
    {
        var state = new ChannelVoiceState
        {
            ChannelId = ChannelId,
            GuildId = GuildId,
            Participants = participants.ToList(),
        };
        _cache.SetEntry(ChannelVoiceState.GetCacheKey(ChannelId), JsonSerializer.Serialize(state));
        _cache.SetEntry(
            ChannelVoiceState.GetUserCacheKey(locationUserId),
            JsonSerializer.Serialize(new { ChannelId, GuildId, DeviceId = DesktopDevice }));
    }

    private static VoiceState Participant(string userId, string? deviceId) => new()
    {
        UserId = userId,
        ChannelId = ChannelId,
        GuildId = GuildId,
        DeviceId = deviceId,
        CfSessionId = "cf-session",
        AudioTrackName = "audio",
    };

    private async Task<List<string>> RosterAsync()
    {
        var raw = await _cache.GetStringAsync(ChannelVoiceState.GetCacheKey(ChannelId));
        var state = JsonSerializer.Deserialize<ChannelVoiceState>(raw!)!;
        return state.Participants.Select(p => p.UserId).ToList();
    }

    private List<(string Method, object?[] Args)> HubMessages() =>
        ((FakeHubClients)_hub.Clients).SentMessages;

    private Task HandleDisconnectAsync(
        string userId, string? deviceId, IDistributedCache? cache = null, LockedJsonCacheStore? store = null)
    {
        var effectiveCache = cache ?? _cache;
        // No blocks: this suite is about voice cleanup, and the block filter only ever removes
        // presence recipients - of which the fake Redis reports none anyway.
        var blocks = PrivacyTestFactory.Blocks(new FakeInvokingMessageBus(), new FakeDistributedCache());
        return _handler.Handle(
            new UserDisconnected(userId, deviceId), _context, _hydrate, effectiveCache,
            store ?? new LockedJsonCacheStore(new FakeDistributedLockService(), effectiveCache), _hub, _bus,
            blocks);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1. The disconnecting device is not the device holding the voice connection
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Disconnect_FromADeviceThatIsNotInVoice_LeavesTheParticipantInTheChannel()
    {
        // The user is talking on desktop. Their phone - same account, second socket, app
        // backgrounded - drops its websocket. Nothing about the desktop voice connection changed.
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
        _cache.SetEntry(ChannelVoiceState.GetHeartbeatCacheKey(UserId), "1");

        await HandleDisconnectAsync(UserId, PhoneDevice);

        // Dropping the heartbeat key is a second, delayed kill: even with the roster entry intact,
        // VoiceHeartbeatCleanupService evicts anyone without one on its next 60s sweep.
        Assert.That(_cache.HasEntry(ChannelVoiceState.GetHeartbeatCacheKey(UserId)), Is.True,
            "the heartbeat belongs to the device that is in voice, not to the one that disconnected");
    }

    [Test]
    public async Task Disconnect_FromTheDeviceThatIsInVoice_StillRemovesTheParticipant()
    {
        // The genuine case, which the device guard must not break.
        SeedVoice(UserId, Participant(UserId, DesktopDevice));
        _cache.SetEntry(ChannelVoiceState.GetHeartbeatCacheKey(UserId), "1");

        await HandleDisconnectAsync(UserId, DesktopDevice);

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Not.Contain(UserId));
            Assert.That(HubMessages().Select(m => m.Method), Does.Contain("guild.voice.UserLeftVoice"));
            Assert.That(_cache.HasEntry(ChannelVoiceState.GetHeartbeatCacheKey(UserId)), Is.False);
        });
    }

    [Test]
    public async Task Disconnect_WithNoDeviceIdOnEitherSide_FallsBackToRemovingTheParticipant()
    {
        // Pre-device-tracking clients and roster entries written before DeviceId existed can't be
        // attributed to a device. Those must keep the old behaviour rather than get stuck in a
        // channel forever.
        SeedVoice(UserId, Participant(UserId, deviceId: null));

        await HandleDisconnectAsync(UserId, deviceId: null);

        Assert.That(await RosterAsync(), Does.Not.Contain(UserId));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. The channel blob is mutated under the channel lock
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Disconnect_RacingAJoin_DoesNotEraseTheJoinerFromTheRoster()
    {
        const string joiner = "user-2";
        SeedVoice(UserId, Participant(UserId, DesktopDevice));

        var channelKey = ChannelVoiceState.GetCacheKey(ChannelId);
        var locks = new TrackingDistributedLockService();

        // A second request commits an unrelated user's join at the worst possible moment: right
        // after the handler has read the channel blob. A writer that takes the same per-key lock
        // cannot actually land there - it blocks until the handler releases - so this models it by
        // deferring the write when the lock is held, and letting it through when it is not (which
        // is what the unlocked version allowed).
        Action? deferredJoin = null;
        var cache = new InterceptingDistributedCache(_cache, channelKey, () =>
        {
            void CommitJoin()
            {
                var raw = _cache.Get(channelKey);
                var state = JsonSerializer.Deserialize<ChannelVoiceState>(
                    System.Text.Encoding.UTF8.GetString(raw!))!;
                state.Participants.Add(Participant(joiner, "joiner-device"));
                _cache.SetEntry(channelKey, JsonSerializer.Serialize(state));
            }

            if (locks.IsHeld(channelKey)) deferredJoin = CommitJoin;
            else CommitJoin();
        });

        await HandleDisconnectAsync(
            UserId, DesktopDevice, cache, new LockedJsonCacheStore(locks, cache));
        deferredJoin?.Invoke();

        // Unserialised, the handler's stale copy won: user-2 got a 200 from POST /join with
        // themselves in the returned roster, the server kept no record of them, nobody was told
        // they joined, and ExchangeParticipantJoined announced nothing in either direction. Both
        // sides were invisible to each other for the rest of the session.
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

        var channelKey = ChannelVoiceState.GetCacheKey(ChannelId);
        var timeline = new List<string>();
        var locks = new TrackingDistributedLockService(timeline);
        var cache = new RecordingDistributedCache(_cache, timeline);

        await HandleDisconnectAsync(UserId, DesktopDevice, cache, new LockedJsonCacheStore(locks, cache));

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
    /// <see cref="IDistributedCache"/> decorator that fires <c>onFirstRead</c> immediately after the
    /// first read of <c>watchedKey</c> returns, and hands the caller the value read <em>before</em>
    /// that callback ran. Models a concurrent writer arriving inside a read-modify-write window,
    /// deterministically and single-threaded.
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
