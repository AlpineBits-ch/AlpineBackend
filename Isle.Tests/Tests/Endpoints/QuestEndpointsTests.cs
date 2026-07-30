using System.Numerics;
using Isle.Api.Endpoints;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers QuestEndpoints - a static, Wolverine-attributed, read-only HTTP surface for the companion
/// website.
/// </summary>
[TestFixture]
public class QuestEndpointsTests
{
    private TestIsleContext _context = null!;
    private string _questId = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = TestIsleContext.Create();

        // QuestInstance.QuestId is a real FK to Quest - every instance below needs a template row
        // to point at, even though the endpoints under test never read the template itself.
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Test Quest", Description = "",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
        _questId = quest.Id;
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private QuestInstance MakeInstance(
        QuestType type = QuestType.Exploration,
        QuestInstanceState state = QuestInstanceState.Active,
        TimeSpan? duration = null)
    {
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = _questId,
            Title = "Find the thing",
            Duration = duration ?? TimeSpan.FromMinutes(30),
            Type = type,
        });

        if (state != QuestInstanceState.Active)
            instance.TryClose(state);

        return instance;
    }

    // ── ActiveQuests ──────────────────────────────────────────────────────

    [Test]
    public async Task ActiveQuests_ExcludesBountiesAndNonActiveInstances()
    {
        var activeExploration = MakeInstance(QuestType.Exploration, QuestInstanceState.Active);
        var activeBounty = MakeInstance(QuestType.Bounty, QuestInstanceState.Active);
        var completedExploration = MakeInstance(QuestType.Exploration, QuestInstanceState.Completed);
        _context.QuestInstances.AddRange(activeExploration, activeBounty, completedExploration);
        await _context.SaveChangesAsync();

        var result = await QuestEndpoints.ActiveQuests(_context, CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { activeExploration.Id }));
    }

    [Test]
    public async Task ActiveQuests_OrdersByExpiresAtAscending()
    {
        var soonToExpire = MakeInstance(duration: TimeSpan.FromMinutes(5));
        var expiresLater = MakeInstance(duration: TimeSpan.FromMinutes(60));
        _context.QuestInstances.AddRange(expiresLater, soonToExpire);
        await _context.SaveChangesAsync();

        var result = await QuestEndpoints.ActiveQuests(_context, CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { soonToExpire.Id, expiresLater.Id }));
    }

    [Test]
    public async Task ActiveQuests_MapsFieldsOntoTheDto()
    {
        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = _questId,
            Title = "Hunt the Rex",
            Duration = TimeSpan.FromMinutes(20),
            Type = QuestType.Hunt,
            RegionId = "south_plains",
            LocationName = "South Plains",
            WorldX = 100,
            WorldY = 200,
            TargetSpecies = "Rex",
            IsAdminSpawned = true,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();

        var result = await QuestEndpoints.ActiveQuests(_context, CancellationToken.None);

        var dto = result.Single();
        Assert.That(dto.QuestId, Is.EqualTo(_questId));
        Assert.That(dto.Title, Is.EqualTo("Hunt the Rex"));
        Assert.That(dto.Type, Is.EqualTo(QuestType.Hunt));
        Assert.That(dto.State, Is.EqualTo(QuestInstanceState.Active));
        Assert.That(dto.RegionId, Is.EqualTo("south_plains"));
        Assert.That(dto.LocationName, Is.EqualTo("South Plains"));
        Assert.That(dto.WorldX, Is.EqualTo(100));
        Assert.That(dto.WorldY, Is.EqualTo(200));
        Assert.That(dto.TargetSpecies, Is.EqualTo("Rex"));
        Assert.That(dto.IsAdminSpawned, Is.True);
        Assert.That(dto.FriendlyId, Does.StartWith(QuestInstance.FriendlyIdPrefix));
    }

    // ── ActiveBounties ────────────────────────────────────────────────────

    [Test]
    public async Task ActiveBounties_OnlyReturnsActiveBounties()
    {
        var activeBounty = MakeInstance(QuestType.Bounty, QuestInstanceState.Active);
        var activeExploration = MakeInstance(QuestType.Exploration, QuestInstanceState.Active);
        var completedBounty = MakeInstance(QuestType.Bounty, QuestInstanceState.Completed);
        _context.QuestInstances.AddRange(activeBounty, activeExploration, completedBounty);
        await _context.SaveChangesAsync();

        var result = await QuestEndpoints.ActiveBounties(_context, CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { activeBounty.Id }));
    }

    // ── History ───────────────────────────────────────────────────────────

    [Test]
    public async Task History_OnlyReturnsNonActiveInstances_NewestFirst()
    {
        var active = MakeInstance(state: QuestInstanceState.Active);
        var completed = MakeInstance(state: QuestInstanceState.Completed);
        var expired = MakeInstance(state: QuestInstanceState.Expired);
        // Stamp EndedAt explicitly so ordering is deterministic rather than relying on two
        // TryClose calls landing in the same SQLite-tick-resolution instant.
        completed.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        expired.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        _context.QuestInstances.AddRange(active, completed, expired);
        await _context.SaveChangesAsync();

        var result = await QuestEndpoints.History(_context, CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { expired.Id, completed.Id }));
    }

    // ── Regions ───────────────────────────────────────────────────────────

    [Test]
    public void Regions_NoPlayersOnline_ReturnsEveryRegionWithZeroPopulation()
    {
        var regionMap = new RegionMap();
        var heatmap = new PopulationHeatmap(new WorldRosterCache(), regionMap);

        var result = QuestEndpoints.Regions(regionMap, heatmap);

        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(r => r.PlayerCount == 0), Is.True);
    }

    [Test]
    public void Regions_PlayersInARegion_ReportsTheirCountAndPopulationShare()
    {
        var regionMap = new RegionMap();
        var roster = new WorldRosterCache();
        roster.Replace([
            new RosterEntry("steam_1", "P1", "Rex", 1f, Vector3.Zero, "south_plains", "South Plains"),
            new RosterEntry("steam_2", "P2", "Rex", 1f, Vector3.Zero, "south_plains", "South Plains"),
            new RosterEntry("steam_3", "P3", "Rex", 1f, Vector3.Zero, "highlands", "Highlands"),
        ]);
        var heatmap = new PopulationHeatmap(roster, regionMap);

        var result = QuestEndpoints.Regions(regionMap, heatmap);

        var southPlains = result.Single(r => r.Id == "south_plains");
        Assert.That(southPlains.PlayerCount, Is.EqualTo(2));
        Assert.That(southPlains.PopulationShare, Is.EqualTo(2.0 / 3.0).Within(0.0001));

        var highlands = result.Single(r => r.Id == "highlands");
        Assert.That(highlands.PlayerCount, Is.EqualTo(1));
    }
}
