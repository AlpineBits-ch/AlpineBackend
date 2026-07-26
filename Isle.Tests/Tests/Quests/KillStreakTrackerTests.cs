using Isle.Api.Services.Quests;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class KillStreakTrackerTests
{
    private KillStreakTracker _tracker = null!;
    private FakeRedisStore _store = null!;

    [SetUp]
    public void SetUp() =>
        _tracker = new KillStreakTracker(RedisTestFactory.Create(out _store), NullLogger<KillStreakTracker>.Instance);

    [Test]
    public async Task RegisterKill_FirstKill_ReturnsOne()
    {
        var streak = await _tracker.RegisterKillAsync("player_1");

        Assert.That(streak, Is.EqualTo(1));
    }

    [Test]
    public async Task RegisterKill_MultipleKills_Accumulates()
    {
        await _tracker.RegisterKillAsync("player_1");
        await _tracker.RegisterKillAsync("player_1");
        var streak = await _tracker.RegisterKillAsync("player_1");

        Assert.That(streak, Is.EqualTo(3));
        Assert.That(await _tracker.GetAsync("player_1"), Is.EqualTo(3));
    }

    [Test]
    public async Task GetAsync_NoKills_ReturnsZero()
    {
        Assert.That(await _tracker.GetAsync("nobody"), Is.EqualTo(0));
    }

    [Test]
    public async Task ResetAsync_ClearsTheStreak()
    {
        await _tracker.RegisterKillAsync("player_1");
        await _tracker.RegisterKillAsync("player_1");

        await _tracker.ResetAsync("player_1");

        Assert.That(await _tracker.GetAsync("player_1"), Is.EqualTo(0));
    }

    [Test]
    public async Task GetLeaderboard_OrdersByKillsDescending()
    {
        await _tracker.RegisterKillAsync("player_1");
        await _tracker.RegisterKillAsync("player_2");
        await _tracker.RegisterKillAsync("player_2");
        await _tracker.RegisterKillAsync("player_2");

        var board = await _tracker.GetLeaderboardAsync();

        Assert.That(board.Select(s => s.PlayerId), Is.EqualTo(new[] { "player_2", "player_1" }));
        Assert.That(board[0].Kills, Is.EqualTo(3));
    }

    [Test]
    public async Task ClearAllAsync_WipesEveryStreak()
    {
        await _tracker.RegisterKillAsync("player_1");
        await _tracker.RegisterKillAsync("player_2");

        await _tracker.ClearAllAsync();

        Assert.That(await _tracker.GetLeaderboardAsync(), Is.Empty);
    }

    [Test]
    public async Task ResetAsync_UnknownPlayer_DoesNotThrow() =>
        Assert.DoesNotThrowAsync(() => _tracker.ResetAsync("nobody"));

    [Test]
    public async Task GetAsync_PruneDropsAKillOlderThanTheWindow()
    {
        await _tracker.RegisterKillAsync("player_1");

        // Backdate the last-kill timestamp past the 20-minute window directly in the fake store —
        // this is the one test that actually exercises PruneAsync's drop path, not just its absence.
        var staleSeconds = DateTimeOffset.UtcNow.Subtract(KillStreakTracker.Window).AddMinutes(-1).ToUnixTimeSeconds();
        _store.SortedSets["isle:spree:last"]["player_1"] = staleSeconds;

        Assert.That(await _tracker.GetAsync("player_1"), Is.EqualTo(0));
    }

    [Test]
    public async Task GetLeaderboard_PruneRemovesAStalePlayerEntirely()
    {
        await _tracker.RegisterKillAsync("player_1");
        await _tracker.RegisterKillAsync("player_2");

        var staleSeconds = DateTimeOffset.UtcNow.Subtract(KillStreakTracker.Window).AddMinutes(-1).ToUnixTimeSeconds();
        _store.SortedSets["isle:spree:last"]["player_1"] = staleSeconds;

        var board = await _tracker.GetLeaderboardAsync();

        Assert.That(board.Select(s => s.PlayerId), Is.EqualTo(new[] { "player_2" }));
    }
}
