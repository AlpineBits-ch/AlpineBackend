using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class BountyCommandTests
{
    private TestIsleContext _context = null!;
    private BountyService _bounties = null!;
    private BountyCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();

        var registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        var ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
        var streaks = new KillStreakTracker(RedisTestFactory.Create(), NullLogger<KillStreakTracker>.Instance);
        var roster = new WorldRosterCache();
        var bus = Substitute.For<IMessageBus>();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);

        _bounties = new BountyService(
            _context, registry, ledger, streaks, announcer, rewards, roster,
            new RegionMap(), bridge, Substitute.For<ISkinStore>(), bus, NullLogger<BountyService>.Instance);

        _command = new BountyCommand(_bounties);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<QuestInstance> AddOpenBountyAsync(
        string? targetSpecies = "Tyrannosaurus", string? locationName = "East Lake",
        double? worldX = 100, double? worldY = 200, TimeSpan? remaining = null)
    {
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "Bounty", Type = QuestType.Bounty,
            Duration = remaining ?? TimeSpan.FromMinutes(20),
            TargetSpecies = targetSpecies, LocationName = locationName, WorldX = worldX, WorldY = worldY,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();
        return instance;
    }

    [Test]
    public async Task ExecuteAsync_NoOpenBounties_ReturnsNobodyMarkedMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("Nobody is marked right now"));
    }

    [Test]
    public async Task ExecuteAsync_OpenBounty_ListsFriendlyIdSpeciesLocationAndCoordinates()
    {
        var instance = await AddOpenBountyAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain($"[{instance.FriendlyId}]"));
        Assert.That(result, Does.Contain("Tyrannosaurus"));
        Assert.That(result, Does.Contain("East Lake"));
        Assert.That(result, Does.Contain("X:"));
        Assert.That(result, Does.Contain("m left"));
    }

    [Test]
    public async Task ExecuteAsync_NoSpecies_FallsBackToGenericDinosaur()
    {
        await AddOpenBountyAsync(targetSpecies: null);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("A dinosaur"));
    }

    [Test]
    public async Task ExecuteAsync_NoLocationName_FallsBackToUnmappedArea()
    {
        await AddOpenBountyAsync(locationName: null, worldX: null, worldY: null);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("an unmapped area"));
    }

    [Test]
    public async Task ExecuteAsync_MultipleOpenBounties_JoinedByPipe()
    {
        await AddOpenBountyAsync(locationName: "East Lake");
        await AddOpenBountyAsync(locationName: "West Ridge");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain(" | "));
        Assert.That(result, Does.Contain("East Lake"));
        Assert.That(result, Does.Contain("West Ridge"));
    }

    [Test]
    public void Name_IsBounty()
    {
        Assert.That(_command.Name, Is.EqualTo("bounty"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }

    [Test]
    public void Cooldown_IsFifteenSeconds()
    {
        Assert.That(_command.Cooldown, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }
}
