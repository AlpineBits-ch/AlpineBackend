using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.Quests;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class QuestsCommandTests
{
    private TestIsleContext _context = null!;
    private QuestProgressLedger _presence = null!;
    private QuestsCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _presence = new QuestProgressLedger(RedisTestFactory.Create(), NullLogger<QuestProgressLedger>.Instance);
        _command = new QuestsCommand(_context, _presence);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<QuestInstance> AddActiveQuestAsync(
        QuestType type = QuestType.Exploration, string? locationName = "East Lake",
        double? worldX = 100, double? worldY = 200, TimeSpan? remaining = null)
    {
        var quest = new Quest
        {
            Id = Quest.GenerateId(), Name = "Explore", Description = "", Type = type,
            Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.Quests.Add(quest);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id, Title = "Explore the Lake", Type = type,
            Duration = remaining ?? TimeSpan.FromMinutes(10),
            LocationName = locationName, WorldX = worldX, WorldY = worldY,
        });
        _context.QuestInstances.Add(instance);
        await _context.SaveChangesAsync();
        return instance;
    }

    [Test]
    public async Task ExecuteAsync_NoActiveQuests_ReturnsNoneRunningMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("No quests are running right now"));
    }

    [Test]
    public async Task ExecuteAsync_BountyInstancesAreExcluded()
    {
        await AddActiveQuestAsync(type: QuestType.Bounty);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("No quests are running right now"));
    }

    [Test]
    public async Task ExecuteAsync_ActiveExplorationQuest_ListsTitleLocationAndCoordinates()
    {
        var instance = await AddActiveQuestAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain($"[{instance.FriendlyId}]"));
        Assert.That(result, Does.Contain("Explore the Lake"));
        Assert.That(result, Does.Contain("East Lake"));
        Assert.That(result, Does.Contain("X:"));
    }

    [Test]
    public async Task ExecuteAsync_UnmappedLocation_FallsBackToGenericPhrase()
    {
        await AddActiveQuestAsync(locationName: null, worldX: null, worldY: null);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("an unmapped area"));
    }

    [Test]
    public async Task ExecuteAsync_VisitorsPresent_IncludesHeadcount()
    {
        var instance = await AddActiveQuestAsync();
        await _presence.CreditPresenceAsync(instance.Id, ["steam_1", "steam_2"]);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("2 there"));
    }

    [Test]
    public async Task ExecuteAsync_MultipleActiveQuests_JoinedByPipe()
    {
        await AddActiveQuestAsync(locationName: "East Lake");
        await AddActiveQuestAsync(locationName: "West Ridge");

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain(" | "));
        Assert.That(result, Does.Contain("East Lake"));
        Assert.That(result, Does.Contain("West Ridge"));
    }

    [Test]
    public void Name_IsQuests()
    {
        Assert.That(_command.Name, Is.EqualTo("quests"));
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
