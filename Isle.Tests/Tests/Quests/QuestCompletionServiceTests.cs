using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class QuestCompletionServiceTests
{
    private TestIsleContext _context = null!;
    private QuestProgressLedger _ledger = null!;
    private WorldRosterCache _roster = null!;
    private IMessageBus _bus = null!;
    private QuestCompletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _ledger = new QuestProgressLedger(RedisTestFactory.Create(), NullLogger<QuestProgressLedger>.Instance);
        _roster = new WorldRosterCache();
        _bus = Substitute.For<IMessageBus>();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);

        _service = new QuestCompletionService(_context, _ledger, rewards, announcer, _roster, _bus, NullLogger<QuestCompletionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<QuestInstance> SpawnAsync(QuestType type, string regionId, TimeSpan duration)
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Test Quest", Description = "", Type = type, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
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

    private static RosterEntry Entry(string steam, string? regionId) =>
        new(steam, "Test", "Rex", 1.0f, default, regionId, "Somewhere");

    // --- TrackPresenceAsync ----------------------------------------------------------------------

    [Test]
    public async Task TrackPresence_CreditsPlayersStandingInAnOpenQuestsRegion()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(30));
        _roster.Replace([Entry("steam_1", "region_a"), Entry("steam_2", "region_b")]);

        var credited = await _service.TrackPresenceAsync();

        Assert.That(credited, Is.EqualTo(1));
        Assert.That(await _ledger.VisitorCountAsync(instance.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task TrackPresence_NoOpenQuests_ReturnsZero()
    {
        _roster.Replace([Entry("steam_1", "region_a")]);

        Assert.That(await _service.TrackPresenceAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task TrackPresence_BountiesAreNotTracked()
    {
        await SpawnAsync(QuestType.Bounty, "region_a", TimeSpan.FromMinutes(30));
        _roster.Replace([Entry("steam_1", "region_a")]);

        Assert.That(await _service.TrackPresenceAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task TrackPresence_ExcludesRosterEntriesWithNoRegion()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(30));
        _roster.Replace([Entry("steam_1", null), Entry("steam_2", "")]);

        var credited = await _service.TrackPresenceAsync();

        Assert.That(credited, Is.EqualTo(0));
        Assert.That(await _ledger.VisitorCountAsync(instance.Id), Is.EqualTo(0));
    }

    // --- TryCompleteHuntAsync --------------------------------------------------------------------

    [Test]
    public async Task TryCompleteHunt_NoOpenHunt_ReturnsFalse()
    {
        var killer = await AddPlayerAsync("steam_killer");

        Assert.That(await _service.TryCompleteHuntAsync(killer.Id), Is.False);
    }

    [Test]
    public async Task TryCompleteHunt_KillerNotInAMatchingRegion_ReturnsFalse()
    {
        await SpawnAsync(QuestType.Hunt, "region_a", TimeSpan.FromMinutes(30));
        var killer = await AddPlayerAsync("steam_killer");
        _roster.Replace([Entry("steam_killer", "region_b")]);

        Assert.That(await _service.TryCompleteHuntAsync(killer.Id), Is.False);
    }

    [Test]
    public async Task TryCompleteHunt_KillerInTheRightRegion_CompletesAndPaysTheKiller()
    {
        var instance = await SpawnAsync(QuestType.Hunt, "region_a", TimeSpan.FromMinutes(30));
        var killer = await AddPlayerAsync("steam_killer");
        _roster.Replace([Entry("steam_killer", "region_a")]);

        var completed = await _service.TryCompleteHuntAsync(killer.Id);

        Assert.That(completed, Is.True);
        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(updated.CompletedByPlayerId, Is.EqualTo(killer.Id));

        var updatedKiller = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == killer.Id);
        Assert.That(updatedKiller.Xp, Is.GreaterThan(25000));
    }

    // --- ResolveDueQuestsAsync -------------------------------------------------------------------

    [Test]
    public async Task ResolveDueQuests_ExplorationWithNoQualifiedVisitors_Expires()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));

        var settled = await _service.ResolveDueQuestsAsync();

        Assert.That(settled, Is.EqualTo(1));
        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Expired));
    }

    [Test]
    public async Task ResolveDueQuests_OneTickShortOfRequiredPresence_StillExpires()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        await AddPlayerAsync("steam_visitor");

        // One tick short of QuestCompletionService.RequiredPresenceTicks (2) — the dwell bar isn't cleared.
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);

        await _service.ResolveDueQuestsAsync();

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Expired));
    }

    [Test]
    public async Task ResolveDueQuests_ExplorationWithQualifiedVisitors_CompletesAndPaysThem()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(-1));
        var visitor = await AddPlayerAsync("steam_visitor");

        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]); // clears RequiredPresenceTicks

        var settled = await _service.ResolveDueQuestsAsync();

        Assert.That(settled, Is.EqualTo(1));
        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Completed));

        var updatedVisitor = await _context.Players.AsNoTracking().FirstAsync(p => p.Id == visitor.Id);
        Assert.That(updatedVisitor.Xp, Is.GreaterThan(25000));
    }

    [Test]
    public async Task ResolveDueQuests_HuntNeverCompletesFromPresenceAlone_AlwaysExpires()
    {
        var instance = await SpawnAsync(QuestType.Hunt, "region_a", TimeSpan.FromMinutes(-1));
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);
        await _ledger.CreditPresenceAsync(instance.Id, ["steam_visitor"]);

        await _service.ResolveDueQuestsAsync();

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Expired), "a hunt is only ever won by a kill, never by presence");
    }

    [Test]
    public async Task ResolveDueQuests_NotYetDue_LeavesItActive()
    {
        var instance = await SpawnAsync(QuestType.Exploration, "region_a", TimeSpan.FromMinutes(30));

        var settled = await _service.ResolveDueQuestsAsync();

        Assert.That(settled, Is.EqualTo(0));
        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Active));
    }

    [Test]
    public async Task ResolveDueQuests_BountiesAreNeverTouched()
    {
        var instance = await SpawnAsync(QuestType.Bounty, "region_a", TimeSpan.FromMinutes(-1));

        await _service.ResolveDueQuestsAsync();

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Active), "bounty expiry is BountyService's job");
    }

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }
}
