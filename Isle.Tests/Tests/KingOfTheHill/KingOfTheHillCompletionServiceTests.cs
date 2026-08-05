using Isle.Api.Games.KingOfTheHill;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Contracts.Events.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillCompletionServiceTests
{
    private TestIsleContext _context = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private RewardGranter _rewards = null!;
    private KingOfTheHillAnnouncer _announcer = null!;
    private IMessageBus _bus = null!;
    private KingOfTheHillCompletionService _service = null!;
    private KingOfTheHillMode _behavior = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(), NullLogger<KingOfTheHillMatchStateStore>.Instance);
        _rewards = new RewardGranter(_context, BridgeTestFactory.CreateDefault(), NullLogger<RewardGranter>.Instance);

        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _announcer = new KingOfTheHillAnnouncer(BridgeTestFactory.CreateDefault(), NullLogger<KingOfTheHillAnnouncer>.Instance, presence, _context);

        _bus = Substitute.For<IMessageBus>();
        _behavior = new KingOfTheHillMode(_ledger, _context, NullLogger<KingOfTheHillMode>.Instance);

        _service = new KingOfTheHillCompletionService(
            _context, _ledger, _stateStore, _rewards, _announcer, _bus, NullLogger<KingOfTheHillCompletionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<GameModeInstance> SpawnRunningMatchAsync(List<RewardConfig>? rewards = null)
    {
        var definition = TestData.KothDefinition(rewards: rewards ?? []);
        _context.GameModeDefinitions.Add(definition);
        await _context.SaveChangesAsync();

        var instance = new GameModeInstance(definition, _behavior);
        instance.Start();
        return instance;
    }

    [Test]
    public async Task ResolveAsync_PersistsAGameModeRunWithFinalStandings()
    {
        var instance = await SpawnRunningMatchAsync();
        var winner = TestData.Player("steam_winner");
        _context.Players.Add(winner);
        await _context.SaveChangesAsync();

        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_winner"]);

        await _service.ResolveAsync(instance, KothTickOutcome.HeldAloneToWin);

        var run = await _context.GameModeRuns.FirstOrDefaultAsync(r => r.DefinitionId == instance.Definition.Id);
        Assert.That(run, Is.Not.Null);
        Assert.That(run!.Results, Has.Count.EqualTo(1));
        Assert.That(run.Results[0].PlayerId, Is.EqualTo(winner.Id));
    }

    [Test]
    public async Task ResolveAsync_PaysWinnerTierRewardsFromTheDefinition()
    {
        var rewards = new List<RewardConfig>
        {
            new() { RewardType = RewardType.Xp, Amount = 5000, AppliesTo = RankRequirement.Winner },
            new() { RewardType = RewardType.Xp, Amount = 500, AppliesTo = RankRequirement.AllParticipants },
        };
        var instance = await SpawnRunningMatchAsync(rewards);
        var winner = TestData.Player("steam_winner");
        _context.Players.Add(winner);
        await _context.SaveChangesAsync();

        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_winner"]);

        await _service.ResolveAsync(instance, KothTickOutcome.HeldAloneToWin);

        var updated = await _context.Players.FirstAsync(p => p.Id == winner.Id);
        // Winner tier nests downward: both the Winner row and the AllParticipants row apply.
        Assert.That(updated.Xp, Is.EqualTo(25000 + 5000 + 500));
    }

    [Test]
    public async Task ResolveAsync_WinnerAgainstOtherContestants_AlsoGetsTheHeldTheHillBonus()
    {
        var instance = await SpawnRunningMatchAsync();
        var winner = TestData.Player("steam_winner");
        _context.Players.Add(winner);
        await _context.SaveChangesAsync();

        // Two other steam ids contested the hill without ever being sole holder.
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_winner", "steam_x", "steam_y"]);
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_winner"]);

        await _service.ResolveAsync(instance, KothTickOutcome.HeldAloneToWin);

        var updated = await _context.Players.FirstAsync(p => p.Id == winner.Id);
        // Base 25000 + bonus 300 XP * 2 other contestants (no Definition.Rewards configured here).
        Assert.That(updated.Xp, Is.EqualTo(25000 + 300 * 2));
    }

    [Test]
    public async Task ResolveAsync_ClearsTheLedgerAndMatchMarker()
    {
        var instance = await SpawnRunningMatchAsync();
        await _stateStore.WriteAsync(new KothMatchState(instance.Definition.Id, instance.InstanceId, instance.StartedAt, []));
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_winner"]);

        await _service.ResolveAsync(instance, KothTickOutcome.TimedOut);

        Assert.That(await _stateStore.ReadAsync(), Is.Null);
        Assert.That(await _ledger.GetStandingsAsync(instance.InstanceId), Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_NobodyEverCredited_PublishesResolvedEventWithNoWinner()
    {
        var instance = await SpawnRunningMatchAsync();

        await _service.ResolveAsync(instance, KothTickOutcome.TimedOut);

        await _bus.Received(1).PublishAsync(Arg.Any<KothMatchResolvedEvent>());
    }

    [Test]
    public async Task CancelAsync_NoMatchRunning_ReturnsFalse()
    {
        Assert.That(await _service.CancelAsync(), Is.False);
    }

    [Test]
    public async Task CancelAsync_RunningMatch_ClearsStateWithoutWritingAGameModeRun()
    {
        var instance = await SpawnRunningMatchAsync();
        await _stateStore.WriteAsync(new KothMatchState(instance.Definition.Id, instance.InstanceId, instance.StartedAt, []));
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_1"]);

        var cancelled = await _service.CancelAsync();

        Assert.That(cancelled, Is.True);
        Assert.That(await _stateStore.ReadAsync(), Is.Null);
        Assert.That(await _ledger.GetStandingsAsync(instance.InstanceId), Is.Empty);
        Assert.That(await _context.GameModeRuns.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task CancelAsync_PublishesCancelledEvent()
    {
        var instance = await SpawnRunningMatchAsync();
        await _stateStore.WriteAsync(new KothMatchState(instance.Definition.Id, instance.InstanceId, instance.StartedAt, []));

        await _service.CancelAsync();

        await _bus.Received(1).PublishAsync(Arg.Any<KothMatchCancelledEvent>());
    }

    [Test]
    public async Task ResolveAsync_FourStandings_EachPaidExactlyTheirOwnTier()
    {
        var rewards = new List<RewardConfig>
        {
            new() { RewardType = RewardType.Xp, Amount = 1000, AppliesTo = RankRequirement.Winner },
            new() { RewardType = RewardType.Xp, Amount = 200, AppliesTo = RankRequirement.Top3 },
            new() { RewardType = RewardType.Xp, Amount = 50, AppliesTo = RankRequirement.AllParticipants },
        };
        var instance = await SpawnRunningMatchAsync(rewards);

        var p1 = TestData.Player("steam_1");
        var p2 = TestData.Player("steam_2");
        var p3 = TestData.Player("steam_3");
        var p4 = TestData.Player("steam_4");
        _context.Players.AddRange(p1, p2, p3, p4);
        await _context.SaveChangesAsync();

        // Strictly decreasing solo-tick counts per player, ticked one at a time so nobody contests
        // anybody else - deliberately never tied, so ranking never depends on the FirstCreditedAt
        // tiebreak's millisecond resolution (covered on its own in KingOfTheHillControlLedgerTests).
        for (var i = 0; i < 4; i++) await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_1"]);
        for (var i = 0; i < 3; i++) await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_2"]);
        for (var i = 0; i < 2; i++) await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_3"]);
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_4"]);

        await _service.ResolveAsync(instance, KothTickOutcome.TimedOut);

        // All four steam ids are recorded as contestants (ApplyPresenceAsync tracks that regardless of
        // solo/contested ticks), so the winner also collects the held-the-hill bonus: 300 XP * 3 others.
        Assert.That((await _context.Players.FirstAsync(p => p.Id == p1.Id)).Xp, Is.EqualTo(25000 + 1000 + 200 + 50 + 300 * 3), "winner nests Top3 and AllParticipants, plus the contestant bonus");
        Assert.That((await _context.Players.FirstAsync(p => p.Id == p2.Id)).Xp, Is.EqualTo(25000 + 200 + 50));
        Assert.That((await _context.Players.FirstAsync(p => p.Id == p3.Id)).Xp, Is.EqualTo(25000 + 200 + 50));
        Assert.That((await _context.Players.FirstAsync(p => p.Id == p4.Id)).Xp, Is.EqualTo(25000 + 50), "rank 4 is participation-only");
    }

    [Test]
    public async Task CancelAsync_MarkerPointsAtANoLongerExistingDefinition_StillClearsLedgerAndMarker()
    {
        var instance = await SpawnRunningMatchAsync();
        await _stateStore.WriteAsync(new KothMatchState("definition_that_was_deleted", instance.InstanceId, instance.StartedAt, []));
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_1"]);

        var cancelled = await _service.CancelAsync();

        Assert.That(cancelled, Is.True);
        Assert.That(await _stateStore.ReadAsync(), Is.Null);
        Assert.That(await _ledger.GetStandingsAsync(instance.InstanceId), Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_StandingWithNoRegisteredPlayerRow_IsSkippedWithoutThrowing()
    {
        // GetStandings already filters unregistered steam ids out before PayOutAsync ever sees them -
        // this exercises the fully-empty-standings path end to end rather than assuming that holds.
        var instance = await SpawnRunningMatchAsync();
        await _ledger.ApplyPresenceAsync(instance.InstanceId, ["steam_unregistered"]);

        Assert.DoesNotThrowAsync(() => _service.ResolveAsync(instance, KothTickOutcome.TimedOut));
    }
}
