using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class QuestAdminCommandTests
{
    private TestIsleContext _context = null!;
    private WorldRosterCache _roster = null!;
    private RegionMap _regions = null!;
    private PopulationHeatmap _heatmap = null!;
    private KillStreakTracker _streaks = null!;
    private QuestAdminCommand _command = null!;

    private const string Usage =
        "Usage: !questadmin bounty player [minutes] [bonusXp] | spawn quest [region] | end Q-id | list | streaks | pop";

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _roster = new WorldRosterCache();
        _regions = new RegionMap();
        _heatmap = new PopulationHeatmap(_roster, _regions);

        var director = new QuestDirector(_context, _heatmap, _roster, _regions, NullLogger<QuestDirector>.Instance);

        var bridge = BridgeTestFactory.CreateDefault();
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);
        var bus = Substitute.For<IMessageBus>();

        var spawner = new QuestSpawner(_context, announcer, bus, NullLogger<QuestSpawner>.Instance);

        var registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        var participantLedger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
        _streaks = new KillStreakTracker(RedisTestFactory.Create(), NullLogger<KillStreakTracker>.Instance);
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);

        var bounties = new BountyService(
            _context, registry, participantLedger, _streaks, announcer, rewards, _roster,
            _regions, bridge, Substitute.For<ISkinStore>(), bus, NullLogger<BountyService>.Instance);

        _command = new QuestAdminCommand(_context, director, spawner, bounties, _streaks, _heatmap);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Quest> AddBountyTemplateAsync()
    {
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
        return quest;
    }

    private async Task<Quest> AddSpawnableExplorationQuestAsync(string name = "Explore")
    {
        var region = _regions.Regions.First();
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = name, Description = "", Type = QuestType.Exploration,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        quest.Locations.Add(new QuestLocation
        {
            Id = QuestLocation.GenerateId(), Title = "Location", Description = "", RegionId = region.Id,
            GeoFence = new GeoFenceData(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
        return quest;
    }

    private async Task<Player> AddPlayerAsync(string steamId, string? inGameName = null)
    {
        var player = TestData.Player(steamId, inGameName);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    // --- dispatch --------------------------------------------------------------------------

    [Test]
    public async Task ExecuteAsync_NoArguments_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Is.EqualTo(Usage));
    }

    [Test]
    public async Task ExecuteAsync_UnknownVerb_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bogus"] });

        Assert.That(result, Is.EqualTo(Usage));
    }

    // --- bounty ------------------------------------------------------------------------------

    [Test]
    public async Task Bounty_MissingTarget_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bounty"] });

        Assert.That(result, Does.Contain("Usage: !questadmin bounty"));
    }

    [Test]
    public async Task Bounty_UnknownPlayer_ReturnsNoMatchMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bounty", "nobody"] });

        Assert.That(result, Does.Contain("No player matches 'nobody'"));
    }

    [Test]
    public async Task Bounty_AmbiguousName_ReturnsAmbiguousMessage()
    {
        await AddPlayerAsync("steam-1", "Dup");
        await AddPlayerAsync("steam-2", "Dup");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bounty", "Dup"] });

        Assert.That(result, Does.Contain("matches more than one player"));
    }

    [Test]
    public async Task Bounty_NoTemplateConfigured_ReturnsCouldNotMarkMessage()
    {
        await AddPlayerAsync("steam-1", "Target");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bounty", "steam-1"] });

        Assert.That(result, Does.Contain("Could not mark"));
    }

    [Test]
    public async Task Bounty_ValidTargetAndTemplate_MarksAndReturnsConfirmation()
    {
        await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam-1", "Target");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bounty", "steam-1", "30", "1000"] });

        Assert.That(result, Does.Contain("Marked Target"));
        Assert.That(result, Does.Contain("30m"));
        Assert.That(result, Does.Contain("bonus"));
        Assert.That(result, Does.Contain("000 XP"));

        var instance = await _context.QuestInstances.FirstOrDefaultAsync(i => i.TargetPlayerId == target.Id);
        Assert.That(instance, Is.Not.Null);
    }

    // --- spawn -------------------------------------------------------------------------------

    [Test]
    public async Task Spawn_MissingQuest_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["spawn"] });

        Assert.That(result, Does.Contain("Usage: !questadmin spawn"));
    }

    [Test]
    public async Task Spawn_NoMatchingQuest_ReturnsNoMatchMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["spawn", "DoesNotExist"] });

        Assert.That(result, Does.Contain("No quest matches 'DoesNotExist'"));
    }

    [Test]
    public async Task Spawn_ValidQuest_SpawnsAndReturnsConfirmation()
    {
        await AddSpawnableExplorationQuestAsync("Explore");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["spawn", "Explore"] });

        Assert.That(result, Does.Contain("Spawned 'Explore' at"));
        Assert.That(result, Does.Contain("Instance"));
    }

    // --- end ---------------------------------------------------------------------------------

    [Test]
    public async Task End_MissingId_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end"] });

        Assert.That(result, Does.Contain("Usage: !questadmin end"));
    }

    [Test]
    public async Task End_UnknownInstance_ReturnsNotFoundMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end", "Q-ZZZZZ"] });

        Assert.That(result, Does.Contain("No quest instance"));
    }

    [Test]
    public async Task End_AlreadyClosedInstance_ReturnsAlreadyStateMessage()
    {
        var quest = await AddSpawnableExplorationQuestAsync();
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "x", Type = QuestType.Exploration, Duration = TimeSpan.FromMinutes(10),
        });
        instance.TryClose(QuestInstanceState.Completed);
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end", instance.FriendlyId] });

        Assert.That(result, Does.Contain("already Completed"));
    }

    [Test]
    public async Task End_OpenExplorationInstance_ClosesAndReturnsConfirmation()
    {
        var quest = await AddSpawnableExplorationQuestAsync();
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "x", Type = QuestType.Exploration, Duration = TimeSpan.FromMinutes(10),
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end", instance.FriendlyId] });

        Assert.That(result, Does.Contain($"Instance {instance.FriendlyId} cancelled."));

        var updated = await _context.QuestInstances.FindAsync(instance.Id);
        Assert.That(updated!.State, Is.EqualTo(QuestInstanceState.Cancelled));
    }

    [Test]
    public async Task End_OpenBountyInstance_CancelsThroughBountyServiceAndUnmarksTarget()
    {
        var template = await AddBountyTemplateAsync();
        var target = await AddPlayerAsync("steam-1", "Target");

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = template.Id, Title = "Bounty", Type = QuestType.Bounty, Duration = TimeSpan.FromMinutes(10),
            TargetPlayerId = target.Id,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end", instance.FriendlyId] });

        Assert.That(result, Does.Contain($"Bounty {instance.FriendlyId} cancelled and target unmarked."));

        var updated = await _context.QuestInstances.FindAsync(instance.Id);
        Assert.That(updated!.State, Is.EqualTo(QuestInstanceState.Cancelled));
    }

    [Test]
    public async Task End_AcceptsRawInstanceId()
    {
        var quest = await AddSpawnableExplorationQuestAsync();
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "x", Type = QuestType.Exploration, Duration = TimeSpan.FromMinutes(10),
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end", instance.Id] });

        Assert.That(result, Does.Contain("cancelled."));
    }

    // --- list --------------------------------------------------------------------------------

    [Test]
    public async Task List_NothingRunning_ReturnsNothingIsRunningMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["list"] });

        Assert.That(result, Is.EqualTo("Nothing is running."));
    }

    [Test]
    public async Task List_OpenInstances_ListsFriendlyIdTypeTitleAndLocation()
    {
        var quest = await AddSpawnableExplorationQuestAsync();
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "Explore the Lake", Type = QuestType.Exploration,
            Duration = TimeSpan.FromMinutes(10), LocationName = "East Lake",
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["list"] });

        Assert.That(result, Does.Contain(instance.FriendlyId));
        Assert.That(result, Does.Contain("Explore the Lake"));
        Assert.That(result, Does.Contain("East Lake"));
    }

    // --- streaks -----------------------------------------------------------------------------

    [Test]
    public async Task Streaks_NoActiveStreaks_ReturnsNoneMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["streaks"] });

        Assert.That(result, Is.EqualTo("No active kill streaks."));
    }

    [Test]
    public async Task Streaks_ActiveStreak_ListsRegisteredPlayerNameAndKillCount()
    {
        var player = await AddPlayerAsync("steam-1", "Killer");
        await _streaks.RegisterKillAsync(player.Id);
        await _streaks.RegisterKillAsync(player.Id);
        await _streaks.RegisterKillAsync(player.Id);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["streaks"] });

        Assert.That(result, Does.Contain("Killer: 3"));
    }

    [Test]
    public async Task Streaks_UnregisteredPlayerId_FallsBackToRawId()
    {
        await _streaks.RegisterKillAsync("player_unregistered");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["streaks"] });

        Assert.That(result, Does.Contain("player_unregistered: 1"));
    }

    // --- pop -----------------------------------------------------------------------------------

    [Test]
    public async Task Pop_NoRosterData_ReturnsNoDataMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["pop"] });

        Assert.That(result, Is.EqualTo("No population data yet."));
    }

    [Test]
    public async Task Pop_RosterPresent_ListsRegionCountsAndOnlineTotal()
    {
        var region = _regions.Regions.First();
        _roster.Replace(
        [
            new RosterEntry("steam-1", "P1", "Rex", 1.0f, default, region.Id, "Somewhere"),
            new RosterEntry("steam-2", "P2", "Rex", 1.0f, default, region.Id, "Somewhere"),
        ]);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["pop"] });

        Assert.That(result, Does.Contain(region.Name));
        Assert.That(result, Does.Contain("2 online"));
    }

    [Test]
    public void IsAdminOnly_IsTrue()
    {
        Assert.That(_command.IsAdminOnly, Is.True);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsFalse()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] }), Is.False);
    }

    [Test]
    public void Name_IsQuestAdmin()
    {
        Assert.That(_command.Name, Is.EqualTo("questadmin"));
    }
}
