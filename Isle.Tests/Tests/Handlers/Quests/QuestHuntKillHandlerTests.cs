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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Quests;

[TestFixture]
public class QuestHuntKillHandlerTests
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
        var bus = Substitute.For<IMessageBus>();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);

        var participation = new QuestParticipationRecorder(_context, NullLogger<QuestParticipationRecorder>.Instance);

        _service = new QuestCompletionService(_context, _ledger, rewards, announcer, _roster, participation, bus, NullLogger<QuestCompletionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Task HandleAsync(string killerId, string victimId) =>
        QuestHuntKillHandler.Handle(new PlayerKillEvent { KilerId = killerId, VictimId = victimId }, _service, CancellationToken.None);

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private async Task<QuestInstance> SpawnHuntAsync(string regionId)
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Hunt", Description = "", Type = QuestType.Hunt, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Quests.Add(quest);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id,
            Type = QuestType.Hunt,
            Title = "Hunt",
            Duration = TimeSpan.FromMinutes(30),
            RegionId = regionId,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();
        return instance;
    }

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

    [Test]
    public async Task Handle_SelfInflictedDeath_ClaimsNoHunt()
    {
        var instance = await SpawnHuntAsync("region_a");
        var player = await AddPlayerAsync("steam_1");
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, "region_a", "Somewhere")]);

        await HandleAsync(player.Id, player.Id);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Active));
    }

    [Test]
    public async Task Handle_KillerInTheHuntsRegion_CompletesTheHuntAndPaysTheKiller()
    {
        var instance = await SpawnHuntAsync("region_a");
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");
        _roster.Replace([new RosterEntry("steam_killer", "Test", "Rex", 1.0f, default, "region_a", "Somewhere")]);

        await HandleAsync(killer.Id, victim.Id);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(updated.CompletedByPlayerId, Is.EqualTo(killer.Id));

        var updatedKiller = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == killer.Id);
        Assert.That(updatedKiller.Xp, Is.GreaterThan(25000));
    }

    [Test]
    public async Task Handle_KillerNotInTheHuntsRegion_LeavesTheHuntOpen()
    {
        var instance = await SpawnHuntAsync("region_a");
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");
        _roster.Replace([new RosterEntry("steam_killer", "Test", "Rex", 1.0f, default, "region_b", "Elsewhere")]);

        await HandleAsync(killer.Id, victim.Id);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Active));
    }

    [Test]
    public async Task Handle_NoOpenHunt_DoesNotThrow()
    {
        var killer = await AddPlayerAsync("steam_killer");
        var victim = await AddPlayerAsync("steam_victim");

        Assert.DoesNotThrowAsync(() => HandleAsync(killer.Id, victim.Id));
    }
}
