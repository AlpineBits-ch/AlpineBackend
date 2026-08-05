using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class BountyServiceTests
{
    private TestIsleContext _context = null!;
    private BountyRegistry _registry = null!;
    private BountyParticipantLedger _ledger = null!;
    private KillStreakTracker _streaks = null!;
    private QuestAnnouncer _announcer = null!;
    private RewardGranter _rewards = null!;
    private WorldRosterCache _roster = null!;
    private IMessageBus _bus = null!;
    private BountyService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        _ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
        _streaks = new KillStreakTracker(RedisTestFactory.Create(), NullLogger<KillStreakTracker>.Instance);
        _roster = new WorldRosterCache();
        _bus = Substitute.For<IMessageBus>();

        var bridge = BridgeTestFactory.CreateDefault();
        _rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);

        _service = new BountyService(
            _context, _registry, _ledger, _streaks, _announcer, _rewards, _roster,
            new RegionMap(), bridge, Substitute.For<ISkinStore>(), _bus, NullLogger<BountyService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<Quest> AddBountyTemplateAsync()
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty, Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
        return quest;
    }

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    // --- TryStartFromSpreeAsync gates -----------------------------------------------------------

    [Test]
    public async Task TryStartFromSpree_BelowMinKills_ReturnsNull()
    {
        var result = await _service.TryStartFromSpreeAsync("player_1", BountyService.MinKillsForSpree - 1);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryStartFromSpree_NotEnoughOnlinePlayers_ReturnsNull()
    {
        // Roster has fewer than MinOnlinePlayersForSpree entries.
        _roster.Replace([]);

        var result = await _service.TryStartFromSpreeAsync("player_1", BountyService.MinKillsForSpree + 5);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryStartFromSpree_NotClearlyAheadOfRunnerUp_ReturnsNull()
    {
        FillRosterWithOnlinePlayers(BountyService.MinOnlinePlayersForSpree);
        await _streaks.RegisterKillAsync("runner_up");
        await _streaks.RegisterKillAsync("runner_up");
        await _streaks.RegisterKillAsync("runner_up");
        await _streaks.RegisterKillAsync("runner_up");
        await _streaks.RegisterKillAsync("runner_up"); // runner_up: 5 kills

        // player_1 has exactly MinKillsForSpree, only 0 ahead of a runner-up on the same count.
        var result = await _service.TryStartFromSpreeAsync("player_1", BountyService.MinKillsForSpree);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryStartFromSpree_ClearlyAhead_StartsABounty()
    {
        FillRosterWithOnlinePlayers(BountyService.MinOnlinePlayersForSpree);
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_leader");

        var streak = BountyService.MinKillsForSpree + BountyService.RequiredLeadOverRunnerUp;
        var instance = await _service.TryStartFromSpreeAsync(target.Id, streak);

        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Type, Is.EqualTo(QuestType.Bounty));
    }

    [Test]
    public async Task TryStartFromSpree_MaxConcurrentAutoBountiesReached_ReturnsNull()
    {
        FillRosterWithOnlinePlayers(BountyService.MinOnlinePlayersForSpree);
        var template = await AddBountyTemplateAsync();
        var alreadyMarked = await AddPlayerAsync("steam_already_marked");
        var newTarget = await AddPlayerAsync("steam_new_target");

        // One automatic bounty already open.
        _context.QuestInstances.Add(QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = template.Id,
            Type = QuestType.Bounty,
            Title = "Bounty",
            Duration = TimeSpan.FromMinutes(20),
            TargetPlayerId = alreadyMarked.Id,
            IsAdminSpawned = false,
        }));
        await _context.SaveChangesAsync();

        var streak = BountyService.MinKillsForSpree + BountyService.RequiredLeadOverRunnerUp;
        var result = await _service.TryStartFromSpreeAsync(newTarget.Id, streak);

        Assert.That(result, Is.Null);
    }

    private void FillRosterWithOnlinePlayers(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(i => new RosterEntry($"steam_filler_{i}", "Filler", "Rex", 1.0f, default, null, "Somewhere"))
            .ToList();
        _roster.Replace(entries);
    }

    // --- StartAsync guards -----------------------------------------------------------------------

    [Test]
    public async Task StartAsync_UnknownPlayer_ReturnsNull()
    {
        var result = await _service.StartAsync("player_does_not_exist", TimeSpan.FromMinutes(20));

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task StartAsync_NoTemplateConfigured_ReturnsNull()
    {
        var target = await AddPlayerAsync("steam_1");

        var result = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task StartAsync_AlreadyOpenBounty_ReturnsNull()
    {
        var template = await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_1");
        _context.QuestInstances.Add(QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = template.Id, Type = QuestType.Bounty, Title = "Bounty",
            Duration = TimeSpan.FromMinutes(20), TargetPlayerId = target.Id,
        }));
        await _context.SaveChangesAsync();

        var result = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task StartAsync_Success_MarksThePlayerInTheRegistry()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_1");

        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        Assert.That(instance, Is.Not.Null);
        Assert.That(await _registry.IsMarkedAsync("steam_1"), Is.True);
    }

    [Test]
    public async Task StartAsync_BonusXp_IsCarriedOnTheInstance()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_1");

        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20), bonusXp: 1000);

        Assert.That(instance!.BonusXp, Is.EqualTo(1000));
    }

    // --- TryClaimAsync ---------------------------------------------------------------------------

    [Test]
    public async Task TryClaim_NoOpenBounty_ReturnsFalse()
    {
        var result = await _service.TryClaimAsync("victim_1", "killer_1");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task TryClaim_ClosesTheBountyAndPaysTheKiller()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var killer = await AddPlayerAsync("steam_killer");

        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));
        Assert.That(instance, Is.Not.Null);

        var claimed = await _service.TryClaimAsync(target.Id, killer.Id);

        Assert.That(claimed, Is.True);
        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updatedInstance.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(updatedInstance.CompletedByPlayerId, Is.EqualTo(killer.Id));

        var updatedKiller = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == killer.Id);
        Assert.That(updatedKiller.Xp, Is.GreaterThan(25000), "the killer should have been paid the winner's share");
    }

    [Test]
    public async Task TryClaim_UnmarksTheTargetAndResetsTheirStreak()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var killer = await AddPlayerAsync("steam_killer");
        await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));
        await _streaks.RegisterKillAsync(target.Id);

        await _service.TryClaimAsync(target.Id, killer.Id);

        Assert.That(await _registry.IsMarkedAsync("steam_target"), Is.False);
        Assert.That(await _streaks.GetAsync(target.Id), Is.EqualTo(0));
    }

    // --- TryResolveOnDeathAsync ------------------------------------------------------------------

    [Test]
    public async Task TryResolveOnDeath_NoOpenBounty_ReturnsFalse()
    {
        Assert.That(await _service.TryResolveOnDeathAsync("player_1"), Is.False);
    }

    [Test]
    public async Task TryResolveOnDeath_RecentAttackerIsRegisteredPlayer_ResolvesAsAKill()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var killer = await AddPlayerAsync("steam_killer");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        await _ledger.RecordAsync(instance!.Id, "steam_killer", 500, DateTimeOffset.UtcNow);

        var resolved = await _service.TryResolveOnDeathAsync(target.Id);

        Assert.That(resolved, Is.True);
        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updatedInstance.CompletedByPlayerId, Is.EqualTo(killer.Id), "the last attacker within the window should be credited with the kill");
    }

    [Test]
    public async Task TryResolveOnDeath_NoRecentAttacker_ResolvesAsNaturalCauses()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        var resolved = await _service.TryResolveOnDeathAsync(target.Id);

        Assert.That(resolved, Is.True);
        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updatedInstance.CompletedByPlayerId, Is.Null, "nobody hit them, so nobody can be credited with a kill");
        Assert.That(updatedInstance.State, Is.EqualTo(QuestInstanceState.Completed));
    }

    // --- CancelForPlayerAsync ---------------------------------------------------------------------

    [Test]
    public async Task CancelForPlayer_NoOpenBounty_ReturnsFalse()
    {
        Assert.That(await _service.CancelForPlayerAsync("player_1", QuestInstanceState.Cancelled), Is.False);
    }

    [Test]
    public async Task CancelForPlayer_OpenBounty_ClosesWithoutPayout()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        var cancelled = await _service.CancelForPlayerAsync(target.Id, QuestInstanceState.Cancelled);

        Assert.That(cancelled, Is.True);
        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updatedInstance.State, Is.EqualTo(QuestInstanceState.Cancelled));
        Assert.That(await _registry.IsMarkedAsync("steam_target"), Is.False);
    }

    // --- ExpireDueBountiesAsync -------------------------------------------------------------------

    [Test]
    public async Task ExpireDueBounties_PaysTheSurvivingTargetAndAnyHunters()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var hunter = await AddPlayerAsync("steam_hunter");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(-1)); // already due
        await _ledger.RecordAsync(instance!.Id, "steam_hunter", 500, DateTimeOffset.UtcNow);

        var expired = await _service.ExpireDueBountiesAsync();

        Assert.That(expired, Is.EqualTo(1));

        var updatedTarget = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == target.Id);
        Assert.That(updatedTarget.Xp, Is.EqualTo(25000), "survival rewards are vitals only, never XP");

        var updatedHunter = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == hunter.Id);
        Assert.That(updatedHunter.Xp, Is.GreaterThan(25000), "the hunter who drew blood should still be paid something");
    }

    [Test]
    public async Task ExpireDueBounties_NotYetDue_LeavesTheBountyOpen()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        var expired = await _service.ExpireDueBountiesAsync();

        Assert.That(expired, Is.EqualTo(0));
        Assert.That(await _registry.IsMarkedAsync("steam_target"), Is.True);
    }

    [Test]
    public async Task ExpireDueBounties_MultipleDueAtOnce_ResolvesAllOfThem()
    {
        await AddBountyTemplateAsync();
        var targetA = await AddPlayerAsync("steam_target_a");
        var targetB = await AddPlayerAsync("steam_target_b");
        await _service.StartAsync(targetA.Id, TimeSpan.FromMinutes(-1));
        await _service.StartAsync(targetB.Id, TimeSpan.FromMinutes(-1));

        var expired = await _service.ExpireDueBountiesAsync();

        Assert.That(expired, Is.EqualTo(2));
        Assert.That(await _registry.IsMarkedAsync("steam_target_a"), Is.False);
        Assert.That(await _registry.IsMarkedAsync("steam_target_b"), Is.False);
    }

    [Test]
    public async Task TryResolveOnDeath_LastAttackerBelowParticipationThreshold_StillCountsAsAPlayerKill()
    {
        // LastAttackerAsync deliberately ignores MinDamageForParticipation - a nibble that finished off
        // an already-wounded target still makes the death a player kill, not a natural-causes one.
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var killer = await AddPlayerAsync("steam_killer");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        await _ledger.RecordAsync(instance!.Id, "steam_killer", BountyParticipantLedger.MinDamageForParticipation - 1, DateTimeOffset.UtcNow);

        var resolved = await _service.TryResolveOnDeathAsync(target.Id);

        Assert.That(resolved, Is.True);
        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updatedInstance.CompletedByPlayerId, Is.EqualTo(killer.Id));
    }

    [Test]
    public async Task CancelForPlayer_UsesTheStatePassedInRatherThanAlwaysCancelled()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam_target");
        var instance = await _service.StartAsync(target.Id, TimeSpan.FromMinutes(20));

        await _service.CancelForPlayerAsync(target.Id, QuestInstanceState.Expired);

        var updatedInstance = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updatedInstance.State, Is.EqualTo(QuestInstanceState.Expired));
    }
}
