using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Progression;

/// <summary>
/// The durable half of quest progress: what survives after the Redis ledger's TTL takes the live
/// half away.
/// </summary>
[TestFixture]
public class QuestParticipationTests
{
    private TestIsleContext _context = null!;
    private QuestProgressLedger _ledger = null!;
    private WorldRosterCache _roster = null!;
    private QuestCompletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _ledger = new QuestProgressLedger(RedisTestFactory.Create(), NullLogger<QuestProgressLedger>.Instance);
        _roster = new WorldRosterCache();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);
        var recorder = new QuestParticipationRecorder(_context, NullLogger<QuestParticipationRecorder>.Instance);

        _service = new QuestCompletionService(
            _context, _ledger, rewards, announcer, _roster, recorder,
            Substitute.For<IMessageBus>(), NullLogger<QuestCompletionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<QuestInstance> SpawnAsync(QuestType type, string regionId, TimeSpan duration)
    {
        var quest = new Quest
        {
            Id = Quest.GenerateId(),
            Name = "Test Quest",
            Description = string.Empty,
            Type = type,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id,
            Type = type,
            Title = "Test Quest",
            Duration = duration,
            RegionId = regionId,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();
        return instance;
    }

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private static RosterEntry Entry(string steam, string? regionId) =>
        new(steam, "Test", "Rex", 1.0f, default, regionId, "Somewhere");

    private Task<QuestParticipation?> ParticipationAsync(string instanceId, string playerId) =>
        _context.QuestParticipations
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.QuestInstanceId == instanceId && row.PlayerId == playerId)!;

    // ── Completion ────────────────────────────────────────────────────────

    [Test]
    public async Task AnExplorationCompletionRecordsTheQualifiedVisitorWithTheirReward()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var visitor = await AddPlayerAsync("steam_visitor");

        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);

        await _service.ResolveDueQuestsAsync();

        var row = await ParticipationAsync(instance.Id, visitor.Id);
        Assert.That(row, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(row!.Outcome, Is.EqualTo(QuestInstanceState.Completed));
            Assert.That(row.Progress, Is.EqualTo(2));
            Assert.That(row.Goal, Is.EqualTo(QuestCompletionService.RequiredPresenceTicks));
            Assert.That(row.Rank, Is.EqualTo(RankRequirement.Winner));
            Assert.That(row.WasPaid, Is.True);
            Assert.That(row.RewardSummary, Is.Not.Empty);
        });
    }

    [Test]
    public async Task AVisitorWhoFellShortIsStillRecorded_WithNoRankAndNoReward()
    {
        // The information a player most wants when a quest paid them nothing.
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var qualified = await AddPlayerAsync("steam_stayed");
        var short_ = await AddPlayerAsync("steam_left");

        await _ledger.CreditPresenceAsync(instance.Id, ["steam_stayed", "steam_left"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_stayed"]);

        await _service.ResolveDueQuestsAsync();

        var fell = await ParticipationAsync(instance.Id, short_.Id);
        Assert.That(fell, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(fell!.Progress, Is.EqualTo(1));
            Assert.That(fell.Rank, Is.Null);
            Assert.That(fell.WasPaid, Is.False);
            Assert.That(fell.RewardSummary, Is.Empty);
            Assert.That(ParticipationAsync(instance.Id, qualified.Id).Result!.WasPaid, Is.True);
        });
    }

    [Test]
    public async Task AHuntRecordsOnlyTheKiller()
    {
        var instance = await SpawnAsync(QuestType.Hunt, "region_a", TimeSpan.FromMinutes(30));
        var killer = await AddPlayerAsync("steam_killer");
        var bystander = await AddPlayerAsync("steam_bystander");

        // The bystander is sampled into the ledger, because hunts are sampled for the headcount.
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_bystander"]);
        _roster.Replace([Entry("steam_killer", "region_a"), Entry("steam_bystander", "region_a")]);

        Assert.That(await _service.TryCompleteHuntAsync(killer.Id), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(ParticipationAsync(instance.Id, killer.Id).Result, Is.Not.Null);
            Assert.That(ParticipationAsync(instance.Id, bystander.Id).Result, Is.Null);
        });

        var row = await ParticipationAsync(instance.Id, killer.Id);
        Assert.Multiple(() =>
        {
            Assert.That(row!.Progress, Is.EqualTo(1));
            Assert.That(row.Goal, Is.EqualTo(1));
            Assert.That(row.Rank, Is.EqualTo(RankRequirement.Winner));
        });
    }

    // ── Expiry ────────────────────────────────────────────────────────────

    [Test]
    public async Task AnExpiredExplorationStillRecordsEveryoneWhoTurnedUp()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var visitor = await AddPlayerAsync("steam_visitor");

        // One tick, one short of the dwell bar, so nothing completes and nothing is paid.
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);

        await _service.ResolveDueQuestsAsync();

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        var row = await ParticipationAsync(instance.Id, visitor.Id);

        Assert.Multiple(() =>
        {
            Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Expired));
            Assert.That(row, Is.Not.Null);
            Assert.That(row!.Outcome, Is.EqualTo(QuestInstanceState.Expired));
            Assert.That(row.Progress, Is.EqualTo(1));
            Assert.That(row.WasPaid, Is.False);
        });
    }

    [Test]
    public async Task AnExpiredQuestNobodyVisitedRecordsNothing()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));

        await _service.ResolveDueQuestsAsync();

        Assert.That(await _context.QuestParticipations.CountAsync(), Is.Zero);
        Assert.That((await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id)).State,
            Is.EqualTo(QuestInstanceState.Expired));
    }

    [Test]
    public async Task AnExpiredHuntRecordsNobody()
    {
        // Standing in the region is not attempting a hunt, and the ledger's dwell number has nothing
        // to do with a hunt's goal of one kill.
        var instance = await SpawnAsync(QuestType.Hunt, "region_a", TimeSpan.FromMinutes(-1));
        await AddPlayerAsync("steam_bystander");
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_bystander"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_bystander"]);

        await _service.ResolveDueQuestsAsync();

        Assert.That(await _context.QuestParticipations.CountAsync(), Is.Zero);
    }

    // ── Negative and edge ─────────────────────────────────────────────────

    [Test]
    public async Task AVisitorWhoIsNotARegisteredPlayerIsSkippedRatherThanFailingTheResolution()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var known = await AddPlayerAsync("steam_known");

        await _ledger.CreditPresenceAsync(instance.Id, ["steam_known", "steam_ghost"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_known", "steam_ghost"]);

        await _service.ResolveDueQuestsAsync();

        Assert.That(await _context.QuestParticipations.CountAsync(), Is.EqualTo(1));
        Assert.That(await ParticipationAsync(instance.Id, known.Id), Is.Not.Null);
    }

    [Test]
    public async Task RecordingIsIdempotentForOneRun()
    {
        // The unique index is the real guard; this pins that a second attempt is a quiet no-op rather
        // than an exception escaping into a resolution that has already paid out.
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var player = await AddPlayerAsync("steam_visitor");
        var recorder = new QuestParticipationRecorder(_context, NullLogger<QuestParticipationRecorder>.Instance);

        var participant = new ResolvedParticipant(player.Id, 2, RankRequirement.Winner, ["500 XP"]);

        await recorder.RecordAsync(instance, [participant], goal: 2);
        await recorder.RecordAsync(instance, [participant], goal: 2);

        Assert.That(await _context.QuestParticipations.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task RecordingNobodyWritesNothing()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var recorder = new QuestParticipationRecorder(_context, NullLogger<QuestParticipationRecorder>.Instance);

        await recorder.RecordAsync(instance, [], goal: 2);

        Assert.That(await _context.QuestParticipations.CountAsync(), Is.Zero);
    }
}
