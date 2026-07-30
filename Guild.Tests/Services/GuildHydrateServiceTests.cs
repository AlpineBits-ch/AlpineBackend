using System.Text.Json;
using Guild.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Guild.Tests.Services;

/// <summary>
/// Covers GuildHydrateService's Redis-backed presence tracking directly against a hand-rolled
/// NSubstitute IDatabase/IBatch (StackExchange.Redis's surface is too large to hand-roll a fake
/// like FakeMessageBus - mirrors RedisTestFactory's approach, but built inline here since these
/// tests need per-test control over batch return values rather than RedisTestFactory's fixed
/// "always empty" default).
/// </summary>
[TestFixture]
public class GuildHydrateServiceTests
{
    private const string GuildId = "guild-1";
    private const string MemberId = "member-1";

    private IDatabase _db = null!;
    private IBatch _batch = null!;
    private GuildHydrateService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = Substitute.For<IDatabase>();
        _batch = Substitute.For<IBatch>();
        _db.CreateBatch(Arg.Any<object?>()).Returns(_batch);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_db);

        _service = new GuildHydrateService(multiplexer, NullLogger<GuildHydrateService>.Instance);
    }

    private static MemberPresenceState MakeState(string memberId = MemberId, string userId = "user-1") => new()
    {
        MemberId = memberId,
        UserId = userId,
        Status = "online",
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    // ══════════════════════════════════════════════════════════════════════ AddPresenceStateAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddPresenceState_InvokesScriptEvaluateWithSerializedState()
    {
        _db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create(1)));

        await _service.AddPresenceStateAsync(GuildId, MakeState());

        await _db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 2 && keys[0] == $"presence:user:{MemberId}" && keys[1] == $"guild:presence:{GuildId}"),
            Arg.Is<RedisValue[]>(args => args.Length == 3 && args[0] == MemberId),
            Arg.Any<CommandFlags>());
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetPresenceStateForMemberAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPresenceStateForMember_NoEntry_ReturnsNull()
    {
        _db.HashGetAsync($"presence:user:{MemberId}", "state", Arg.Any<CommandFlags>()).Returns(Task.FromResult(RedisValue.Null));

        var result = await _service.GetPresenceStateForMemberAsync(MemberId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPresenceStateForMember_EntryExists_ReturnsDeserializedState()
    {
        var state = MakeState();
        _db.HashGetAsync($"presence:user:{MemberId}", "state", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue(JsonSerializer.Serialize(state))));

        var result = await _service.GetPresenceStateForMemberAsync(MemberId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserId, Is.EqualTo(state.UserId));
        Assert.That(result.Status, Is.EqualTo("online"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // PruneExpiredMembersAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PruneExpiredMembers_RemovesScoresUpToNow()
    {
        await _service.PruneExpiredMembersAsync(GuildId);

        await _db.Received(1).SortedSetRemoveRangeByScoreAsync(
            $"guild:presence:{GuildId}", Arg.Is<double>(d => d == 0), Arg.Any<double>(), Arg.Any<Exclude>(), Arg.Any<CommandFlags>());
    }

    // ══════════════════════════════════════════════════════════════════════
    // RemovePresenceStateAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RemovePresenceState_DeletesKeyAndZsetEntryViaBatch()
    {
        _batch.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(true));
        _batch.SortedSetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(true));

        await _service.RemovePresenceStateAsync(GuildId, MemberId);

        await _batch.Received(1).KeyDeleteAsync($"presence:user:{MemberId}", Arg.Any<CommandFlags>());
        await _batch.Received(1).SortedSetRemoveAsync($"guild:presence:{GuildId}", MemberId, Arg.Any<CommandFlags>());
        _batch.Received(1).Execute();
    }

    // ══════════════════════════════════════════════════════════════════════ GetGuildPresenceAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetGuildPresence_NoActiveUsers_ReturnsEmptyWithoutBatching()
    {
        _db.SortedSetRangeByScoreAsync(Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(), Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisValue>()));

        var result = await _service.GetGuildPresenceAsync(GuildId);

        Assert.That(result, Is.Empty);
        _db.DidNotReceive().CreateBatch(Arg.Any<object?>());
    }

    [Test]
    public async Task GetGuildPresence_ActiveUsers_ReturnsDeserializedStatesSkippingMissingHashes()
    {
        _db.SortedSetRangeByScoreAsync(Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(), Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { "user-a", "user-b" }));

        var stateA = MakeState(memberId: "user-a", userId: "user-a");
        _batch.HashGetAllAsync("presence:user:user-a", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { new HashEntry("state", JsonSerializer.Serialize(stateA)) }));
        // "user-b" has expired/missing its hash entirely - simulates the fallback-TTL race where
        // the ZSET score hasn't been pruned yet but the hash key already expired.
        _batch.HashGetAllAsync("presence:user:user-b", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<HashEntry>()));

        var result = await _service.GetGuildPresenceAsync(GuildId);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].UserId, Is.EqualTo("user-a"));
        _batch.Received(1).Execute();
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetPresenceByMemberIdsAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPresenceByMemberIds_ReturnsDictionaryKeyedByMemberId()
    {
        _db.SortedSetRangeByScoreAsync(Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(), Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisValue>()));

        var state = MakeState();
        _batch.HashGetAllAsync($"presence:user:{MemberId}", Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { new HashEntry("state", JsonSerializer.Serialize(state)) }));

        var result = await _service.GetPresenceByMemberIdsAsync(GuildId, [MemberId]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[MemberId].Status, Is.EqualTo("online"));
    }
}
