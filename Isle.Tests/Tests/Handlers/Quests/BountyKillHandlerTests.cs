using Isle.Api.Handlers.Quests;
using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Player;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Quests;

[TestFixture]
public class BountyKillHandlerTests
{
    private TestIsleContext _context = null!;
    private BountyRegistry _registry = null!;
    private BountyParticipantLedger _ledger = null!;
    private KillStreakTracker _streaks = null!;
    private WorldRosterCache _roster = null!;
    private BountyService _bounties = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        _ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
        _streaks = new KillStreakTracker(RedisTestFactory.Create(), NullLogger<KillStreakTracker>.Instance);
        _roster = new WorldRosterCache();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);
        var bus = Substitute.For<IMessageBus>();

        _bounties = new BountyService(
            _context, _registry, _ledger, _streaks, announcer, rewards, _roster,
            new RegionMap(), bridge, Substitute.For<ISkinStore>(), bus, NullLogger<BountyService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Task HandleAsync(string killerId, string victimId) =>
        BountyKillHandler.Handle(
            new PlayerKillEvent { KilerId = killerId, VictimId = victimId },
            _streaks, _bounties, NullLogger<BountyKillHandler>.Instance, CancellationToken.None);

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private async Task AddBountyTemplateAsync()
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty, Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
    }

    private void FillRosterWithOnlinePlayers(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(i => new RosterEntry($"steam_filler_{i}", "Filler", "Rex", 1.0f, default, null, "Somewhere"))
            .ToList();
        _roster.Replace(entries);
    }

    // --- Guard clauses ------------------------------------------------------------------------------

    [Test]
    public void Handle_BlankKillerId_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => HandleAsync("", "victim_1"));
    }

    [Test]
    public void Handle_BlankVictimId_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => HandleAsync("killer_1", ""));
    }

    // --- Self-kill ------------------------------------------------------------------------------------

    [Test]
    public async Task Handle_SelfKill_ResetsTheVictimsStreakWithoutClaimingABounty()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        await _streaks.RegisterKillAsync(player.Id);
        await _streaks.RegisterKillAsync(player.Id);
        await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        await HandleAsync(player.Id, player.Id);

        Assert.That(await _streaks.GetAsync(player.Id), Is.EqualTo(0));
        // The bounty on the "victim" must still be open - a self-kill claims nothing.
        Assert.That(await _bounties.FindOpenBountyAsync(player.Id), Is.Not.Null);
    }

    // --- Ordinary kill: claim + streak bump --------------------------------------------------------

    [Test]
    public async Task Handle_KillerKillsAMarkedVictim_ClosesTheBountyAndPaysTheKiller()
    {
        await AddBountyTemplateAsync();
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");
        var instance = await _bounties.StartAsync(victim.Id, TimeSpan.FromMinutes(20));
        Assert.That(instance, Is.Not.Null);

        await HandleAsync(killer.Id, victim.Id);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(updated.CompletedByPlayerId, Is.EqualTo(killer.Id));
    }

    [Test]
    public async Task Handle_KillerKillsAnUnmarkedVictim_StillBumpsTheKillersStreak()
    {
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");

        await HandleAsync(killer.Id, victim.Id);

        Assert.That(await _streaks.GetAsync(killer.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task Handle_KillerKillsVictim_ResetsTheVictimsOwnStreak()
    {
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");
        await _streaks.RegisterKillAsync(victim.Id);
        await _streaks.RegisterKillAsync(victim.Id);

        await HandleAsync(killer.Id, victim.Id);

        Assert.That(await _streaks.GetAsync(victim.Id), Is.EqualTo(0));
    }

    // --- Spree detection ------------------------------------------------------------------------------

    [Test]
    public async Task Handle_StreakBelowSpreeThreshold_DoesNotAutoMarkTheKiller()
    {
        await AddBountyTemplateAsync();
        FillRosterWithOnlinePlayers(BountyService.MinOnlinePlayersForSpree);
        var killer = await AddPlayerAsync("steam_killer");

        for (var i = 0; i < BountyService.MinKillsForSpree - 1; i++)
        {
            var victim = await AddPlayerAsync($"steam_victim_{i}");
            await HandleAsync(killer.Id, victim.Id);
        }

        Assert.That(await _registry.IsMarkedAsync(killer.SteamId), Is.False);
    }

    [Test]
    public async Task Handle_KillerReachesSpreeThreshold_AutomaticallyOpensABountyOnThem()
    {
        await AddBountyTemplateAsync();
        FillRosterWithOnlinePlayers(BountyService.MinOnlinePlayersForSpree);
        var killer = await AddPlayerAsync("steam_killer");

        for (var i = 0; i < BountyService.MinKillsForSpree; i++)
        {
            var victim = await AddPlayerAsync($"steam_victim_{i}");
            await HandleAsync(killer.Id, victim.Id);
        }

        Assert.That(await _registry.IsMarkedAsync(killer.SteamId), Is.True);
    }
}
