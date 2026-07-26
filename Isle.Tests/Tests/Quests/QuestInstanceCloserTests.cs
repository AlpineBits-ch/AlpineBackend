using Isle.Api.Services.Quests;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class QuestInstanceCloserTests
{
    private TestIsleContext _context = null!;

    [SetUp]
    public void SetUp() => _context = TestIsleContext.Create();

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<QuestInstance> SpawnAsync()
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Test", Description = "", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Quests.Add(quest);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = quest.Id,
            Title = "Test",
            Duration = TimeSpan.FromMinutes(30),
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

    [Test]
    public async Task TryCloseAtomically_ActiveInstance_Succeeds()
    {
        var instance = await SpawnAsync();
        var player = await AddPlayerAsync("steam_1");

        var closed = await _context.TryCloseQuestAtomicallyAsync(instance, QuestInstanceState.Completed, player.Id, CancellationToken.None);

        Assert.That(closed, Is.True);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(instance.CompletedByPlayerId, Is.EqualTo(player.Id));
        Assert.That(instance.EndedAt, Is.Not.Null);
    }

    [Test]
    public async Task TryCloseAtomically_AlreadyClosed_SecondCallerLosesTheRace()
    {
        var instance = await SpawnAsync();
        var firstPlayer = await AddPlayerAsync("steam_1");
        var secondPlayer = await AddPlayerAsync("steam_2");

        var firstClose = await _context.TryCloseQuestAtomicallyAsync(instance, QuestInstanceState.Completed, firstPlayer.Id, CancellationToken.None);
        Assert.That(firstClose, Is.True);

        // A second attempt against the same now-closed row — the conditional UPDATE matches nothing.
        var secondClose = await _context.TryCloseQuestAtomicallyAsync(instance, QuestInstanceState.Expired, secondPlayer.Id, CancellationToken.None);

        Assert.That(secondClose, Is.False);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Completed), "the first close must win");
        Assert.That(instance.CompletedByPlayerId, Is.EqualTo(firstPlayer.Id));
    }

    [Test]
    public async Task TryCloseAtomically_ToActiveState_ReturnsFalseWithoutTouchingTheRow()
    {
        var instance = await SpawnAsync();

        var closed = await _context.TryCloseQuestAtomicallyAsync(instance, QuestInstanceState.Active, null, CancellationToken.None);

        Assert.That(closed, Is.False);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Active));
    }
}
