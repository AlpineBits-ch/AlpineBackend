using System.Net;
using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Wolverine;

namespace Guild.Tests.Services;

/// <summary>The sweep is now the only thing that removes a voice participant.</summary>
[TestFixture]
public class VoiceHeartbeatCleanupServiceTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string Ghost = "user-ghost";
    private const string Live = "user-live";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ───────────────────────────────────────────────────────────────

    private void SeedRoom(params string[] userIds)
    {
        var room = new VoiceRoom
        {
            RoomId = ChannelId,
            Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants = userIds
                .Select(id => new VoiceParticipant { UserId = id, DeviceId = $"device-{id}" })
                .ToList(),
        };
        _cache.SetEntry(room.Key.CacheKey, JsonSerializer.Serialize(room));

        foreach (var id in userIds)
        {
            _cache.SetEntry(
                ChannelVoiceState.GetUserCacheKey(id),
                JsonSerializer.Serialize(new { ChannelId, GuildId, DeviceId = $"device-{id}" }));
        }
    }

    /// <summary>Gives <paramref name="userId"/> a heartbeat, which is the whole difference between
    /// a participant and a ghost as far as the sweep is concerned.</summary>
    private void SeedHeartbeat(string userId) =>
        _cache.SetEntry(VoiceReconciler.LivenessKey(userId), VoiceRoomKey.Channel(ChannelId).ToString());

    private async Task<List<string>> RosterAsync()
    {
        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        return room?.Participants.Select(p => p.UserId).ToList() ?? [];
    }

    private Task SweepAsync() => BuildService().EvictStaleParticipantsAsync(CancellationToken.None);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sweep_RemovesAParticipantWhoseHeartbeatHasExpired()
    {
        SeedRoom(Ghost);

        await SweepAsync();

        // With the disconnect handler no longer evicting anybody, this is the entire eviction path.
        Assert.That(await RosterAsync(), Does.Not.Contain(Ghost));
    }

    [Test]
    public async Task Sweep_RemovesTheEvictedParticipantsVoiceLocationPointer()
    {
        SeedRoom(Ghost);

        await SweepAsync();

        // The pointer used to be dropped by the disconnect handler.
        Assert.That(_cache.HasEntry(ChannelVoiceState.GetUserCacheKey(Ghost)), Is.False);
    }

    [Test]
    public async Task Sweep_LeavesAParticipantWhoIsStillHeartbeating()
    {
        SeedRoom(Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Contain(Live));
            Assert.That(_cache.HasEntry(ChannelVoiceState.GetUserCacheKey(Live)), Is.True);
        });
    }

    [Test]
    public async Task Sweep_TakesOnlyTheGhostOutOfAMixedRoom()
    {
        // The eviction is per participant, not per room: a room containing one dead client must not
        // lose the people still in it.
        SeedRoom(Ghost, Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Contain(Live));
            Assert.That(roster, Does.Not.Contain(Ghost));
        });
    }

    [Test]
    public async Task Sweep_WithNoRoomsAtAll_DoesNothing()
    {
        // Negative case: the scan finds no keys, which is the steady state of a quiet deployment.
        Assert.DoesNotThrowAsync(SweepAsync);
        await Task.CompletedTask;
    }

    // ── Fixture plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// The service reads the room keys straight off Redis with a <c>KEYS</c>-style scan, so the
    /// multiplexer has to answer <c>GetServer().Keys()</c> with whatever the fake cache holds.
    /// </summary>
    private VoiceHeartbeatCleanupService BuildService()
    {
        var keys = _cache.Keys
            .Where(k => k.StartsWith("voice:room:", StringComparison.Ordinal))
            .Select(k => (RedisKey)k)
            .ToArray();

        var server = Substitute.For<IServer>();
        server.Keys(Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(), Arg.Any<long>(),
                Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns(keys);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetEndPoints(Arg.Any<bool>())
            .Returns([new IPEndPoint(IPAddress.Loopback, 6379)]);
        multiplexer.GetServer(Arg.Any<System.Net.EndPoint>(), Arg.Any<object?>()).Returns(server);

        var locks = new FakeDistributedLockService();

        return new VoiceHeartbeatCleanupService(
            multiplexer,
            _cache,
            VoiceTestHarness.StoreFor(_cache, locks),
            new VoiceAnnouncer(new FakeHubContext()),
            new GuildVoiceActivityStore(locks, _cache),
            new StreamViewerStore(locks, _cache),
            new FakeHubContext(),
            new SingleContextScopeFactory(_context),
            NullLogger<VoiceHeartbeatCleanupService>.Instance);
    }

    /// <summary>Hands out the fixture's own DbContext and a throwaway bus, so the guild fan-out at
    /// the end of a sweep can run without a real container.</summary>
    private sealed class SingleContextScopeFactory(TestGuildContext context) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(Guild.Persistence.Persistence.MicroserviceContext) ? context
            : serviceType == typeof(IMessageBus) ? new FakeMessageBus()
            : null;
    }
}
