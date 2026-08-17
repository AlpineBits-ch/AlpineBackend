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

    /// <summary>The hub the announcer pushes through.</summary>
    private FakeHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _hub = new FakeHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>How long ago a seeded participant joined.</summary>
    private static readonly TimeSpan Settled = VoiceSubscriptionOptions.Default.IdleRoomGrace * 2;

    private void SeedRoom(params string[] userIds)
    {
        var room = new VoiceRoom
        {
            RoomId = ChannelId,
            Kind = VoiceRoomKind.Channel,
            GuildId = GuildId,
            Participants = userIds
                .Select(id => new VoiceParticipant
                {
                    UserId = id,
                    DeviceId = $"device-{id}",
                    JoinedAt = DateTime.UtcNow - Settled,
                })
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

    /// <summary>Puts <paramref name="userId"/> on the roster as of now, which is what a join, a
    /// rejoin or a moderator move all look like a moment after they land.</summary>
    private async Task SeedJustJoinedAsync(string userId)
    {
        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        room!.Participants.Add(new VoiceParticipant
        {
            UserId = userId,
            DeviceId = $"device-{userId}",
            JoinedAt = DateTime.UtcNow,
        });
        _cache.SetEntry(room.Key.CacheKey, JsonSerializer.Serialize(room));
    }

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

    /// <summary>
    /// A room emptied by the sweep has nobody left to run the leave path that closes it, so before
    /// this it survived on the four hour sliding TTL - carrying a roster, an instance id, an
    /// attention blob and a viewer table that every later sweep re-read, and handing the next session
    /// a version counter belonging to a conversation that had ended.
    /// </summary>
    [Test]
    public async Task Sweep_ClosesARoomItJustEmptied()
    {
        SeedRoom(Ghost);

        await SweepAsync();

        Assert.That(
            await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId)), Is.Null);
    }

    [Test]
    public async Task Sweep_LeavesARoomItOnlyPartlyEmptied()
    {
        SeedRoom(Ghost, Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        Assert.That(
            await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId)), Is.Not.Null,
            "somebody is still in it, and reaping a live room drops a call");
    }

    [Test]
    public async Task Sweep_WithNoRoomsAtAll_DoesNothing()
    {
        // Negative case: the scan finds no keys, which is the steady state of a quiet deployment.
        Assert.DoesNotThrowAsync(SweepAsync);
        await Task.CompletedTask;
    }

    // ── Telling the evicted ───────────────────────────────────────────────────

    /// <summary>The person removed is the person who has to hear about it.</summary>
    [Test]
    public async Task Sweep_TellsTheParticipantItEvicted()
    {
        SeedRoom(Ghost, Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        Assert.That(ResyncRecipients("roomGone"), Does.Contain(Ghost));
    }

    /// <summary>
    /// The case from the incident: everybody in the room loses liveness at once - a gateway
    /// rollout, a Redis blip - and the sweep takes all of them in a single pass.
    /// </summary>
    [Test]
    public async Task Sweep_TellsEveryoneWhenItEvictsTheWholeRoom()
    {
        const string SecondGhost = "user-ghost-2";
        SeedRoom(Ghost, SecondGhost);

        await SweepAsync();

        Assert.That(ResyncRecipients("roomGone"), Is.EquivalentTo(new[] { Ghost, SecondGhost }));
    }

    [Test]
    public async Task Sweep_TellsTheSurvivorsAboutTheRosterChangeInsteadOfRoomGone()
    {
        SeedRoom(Ghost, Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        Assert.Multiple(() =>
        {
            // Still in the room, so "the roster moved", never "your room is gone" - which would
            // have a healthy client tear down a call it is sitting in.
            Assert.That(ResyncRecipients("participantsEvicted"), Does.Contain(Live));
            Assert.That(ResyncRecipients("roomGone"), Does.Not.Contain(Live));
        });
    }

    [Test]
    public async Task Sweep_SaysNothingAtAllWhenItEvictsNobody()
    {
        SeedRoom(Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ResyncRecipients("roomGone"), Is.Empty);
            Assert.That(ResyncRecipients("participantsEvicted"), Is.Empty,
                "a quiet sweep that announced anything would push a refetch at every participant "
                + "of every room once a minute");
        });
    }

    // ── The join grace ────────────────────────────────────────────────────────

    /// <summary>
    /// Not every path onto a roster claims liveness - a moderator move puts somebody in a channel
    /// without their client doing anything - so a participant who has only just arrived is
    /// indistinguishable here from one who is gone.
    /// </summary>
    [Test]
    public async Task Sweep_SparesAnArrivalWhoHasNotHadTimeToHeartbeatYet()
    {
        const string Arrival = "user-arrival";
        SeedRoom(Live);
        SeedHeartbeat(Live);
        await SeedJustJoinedAsync(Arrival);

        await SweepAsync();

        var roster = await RosterAsync();
        Assert.Multiple(() =>
        {
            Assert.That(roster, Does.Contain(Arrival));
            Assert.That(ResyncRecipients("roomGone"), Does.Not.Contain(Arrival));
        });
    }

    [Test]
    public async Task Sweep_StillTakesASettledParticipantWithNoHeartbeat()
    {
        // The other side of the grace: it is a window after joining, not a permanent exemption for
        // anybody the sweep has not seen heartbeat.
        SeedRoom(Ghost);
        await SeedJustJoinedAsync("user-arrival");

        await SweepAsync();

        Assert.That(await RosterAsync(), Does.Not.Contain(Ghost));
    }

    /// <summary>
    /// A seat somebody left behind in this channel is not kept alive by their being live in a
    /// different one.
    /// </summary>
    [Test]
    public async Task Sweep_TakesASeatWhoseOwnerIsHeartbeatingForADifferentRoom()
    {
        SeedRoom(Ghost);
        // What their client is really asserting: alive, in the channel they moved to.
        _cache.SetEntry(
            VoiceReconciler.LivenessKey(Ghost),
            VoiceRoomKey.Channel("channel-they-moved-to").ToString());

        await SweepAsync();

        Assert.That(await RosterAsync(), Does.Not.Contain(Ghost));
    }

    /// <summary>The other direction, so the check above cannot be satisfied by evicting everybody:
    /// a heartbeat naming this room still spares its owner.</summary>
    [Test]
    public async Task Sweep_KeepsASeatWhoseOwnerIsHeartbeatingForThisRoom()
    {
        SeedRoom(Live);
        SeedHeartbeat(Live);

        await SweepAsync();

        Assert.That(await RosterAsync(), Does.Contain(Live));
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
            new VoiceAnnouncer(_hub),
            VoiceTestHarness.ReconcilerFor(_cache, locks, _hub),
            new GuildVoiceActivityStore(locks, _cache),
            new StreamViewerStore(locks, _cache),
            _hub,
            new SingleContextScopeFactory(_context),
            VoiceSubscriptionOptions.Default,
            VoiceTestHarness.ServiceFor(_cache, locks, _hub),
            // Configured, but with nothing in the room to prune: the share check is skipped entirely
            // for a roster carrying no shares, which is every room in this fixture.
            new FakeVoiceSfu(),
            NullLogger<VoiceHeartbeatCleanupService>.Instance);
    }

    /// <summary>Everyone sent a <c>Resync</c> carrying <paramref name="reason"/>.</summary>
    private List<string> ResyncRecipients(string reason) =>
        ((FakeHubClients)_hub.Clients).SentToUsers
            .Where(s => s.Method == "guild.voice.Resync"
                        && s.Args.Length > 0
                        && s.Args[0] is IReadOnlyDictionary<string, object?> payload
                        && payload.TryGetValue("reason", out var actual)
                        && actual as string == reason)
            .SelectMany(s => s.UserIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
