using Isle.Api.Services.Quests;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class QuestDirectorTests
{
    private TestIsleContext _context = null!;
    private WorldRosterCache _roster = null!;
    private QuestDirector _director = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _roster = new WorldRosterCache();
        var regions = new RegionMap();
        var heatmap = new PopulationHeatmap(_roster, regions);
        _director = new QuestDirector(_context, heatmap, _roster, regions, NullLogger<QuestDirector>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<Quest> AddSpawnableQuestAsync()
    {
        var region = new RegionMap().Regions.First();
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Explore", Description = "", Type = QuestType.Exploration,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        quest.Locations.Add(new Isle.Domain.Entity.QuestLocation
        {
            Id = Isle.Domain.Entity.QuestLocation.GenerateId(),
            Title = "Location",
            Description = "",
            RegionId = region.Id,
            GeoFence = new Isle.Domain.ValueObjects.GeoFenceData(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
        return quest;
    }

    [Test]
    public async Task ChooseAsync_RosterNeverRefreshed_ReturnsNull()
    {
        await AddSpawnableQuestAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_NobodyOnline_ReturnsNull()
    {
        _roster.Replace([]);
        await AddSpawnableQuestAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_MaxConcurrentQuestsAlreadyOpen_ReturnsNull()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var quest = await AddSpawnableQuestAsync();

        for (var i = 0; i < QuestDirector.MaxConcurrentQuests; i++)
        {
            _context.QuestInstances.Add(QuestInstance.Spawn(new SpawnQuestInstanceArgs
            {
                QuestId = quest.Id, Type = QuestType.Exploration, Title = "x", Duration = TimeSpan.FromMinutes(30),
            }));
        }
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_NoEligibleTemplates_ReturnsNull()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        // No quest templates added at all.

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_EligibleTemplate_ReturnsACandidate()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var quest = await AddSpawnableQuestAsync();

        var candidate = await _director.ChooseAsync();

        Assert.That(candidate, Is.Not.Null);
        Assert.That(candidate!.Quest.Id, Is.EqualTo(quest.Id));
    }

    [Test]
    public async Task ChooseAsync_DisabledTemplate_IsNeverEligible()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var quest = await AddSpawnableQuestAsync();
        quest.Enabled = false;
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_BountyTemplatesAreNeverPickedAtRandom()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var region = new RegionMap().Regions.First();
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        quest.Locations.Add(new Isle.Domain.Entity.QuestLocation
        {
            Id = Isle.Domain.Entity.QuestLocation.GenerateId(),
            Title = "Location",
            Description = "",
            RegionId = region.Id,
            GeoFence = new Isle.Domain.ValueObjects.GeoFenceData(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseForcedAsync_FindsQuestByName_IgnoringCase()
    {
        var quest = await AddSpawnableQuestAsync();

        var candidate = await _director.ChooseForcedAsync(quest.Name.ToUpperInvariant(), null);

        Assert.That(candidate, Is.Not.Null);
        Assert.That(candidate!.Quest.Id, Is.EqualTo(quest.Id));
    }

    [Test]
    public async Task ChooseForcedAsync_UnknownQuest_ReturnsNull()
    {
        Assert.That(await _director.ChooseForcedAsync("does-not-exist", null), Is.Null);
    }

    [Test]
    public async Task ChooseForcedAsync_QuestWithNoLocations_ReturnsNull()
    {
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "NoLocations", Description = "", Type = QuestType.Exploration,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseForcedAsync(quest.Name, null), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_TemplateWithAnAlreadyOpenInstance_IsExcludedEvenThoughEnabled()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var quest = await AddSpawnableQuestAsync();
        _context.QuestInstances.Add(QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Type = QuestType.Exploration, Title = quest.Name, Duration = TimeSpan.FromMinutes(30),
        }));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_TemplateStillOnItsOwnCooldown_IsIneligibleEvenIfEnabledAndOnline()
    {
        _roster.Replace([new RosterEntry("steam_1", "Test", "Rex", 1.0f, default, null, "Somewhere")]);
        var quest = await AddSpawnableQuestAsync();
        quest.Cooldown = TimeSpan.FromMinutes(45);
        quest.LastSpawnedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }
}
