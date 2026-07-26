using Isle.Api.Services.Quests;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class BountyRegistryTests
{
    private BountyRegistry _registry = null!;

    [SetUp]
    public void SetUp() =>
        _registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);

    private static BountyMark Mark(string steam, DateTimeOffset? expiresAt = null) =>
        new("qi_1", "player_1", steam, "Rex", expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(20));

    [Test]
    public async Task MarkThenGetBySteam_RoundTrips()
    {
        await _registry.MarkAsync(Mark("steam_1"));

        var mark = await _registry.GetBySteamAsync("steam_1");

        Assert.That(mark, Is.Not.Null);
        Assert.That(mark!.SteamId, Is.EqualTo("steam_1"));
        Assert.That(mark.Species, Is.EqualTo("Rex"));
    }

    [Test]
    public async Task GetBySteam_UnmarkedPlayer_ReturnsNull()
    {
        Assert.That(await _registry.GetBySteamAsync("steam_unmarked"), Is.Null);
    }

    [Test]
    public async Task GetBySteam_NullOrBlankSteamId_ReturnsNull()
    {
        Assert.That(await _registry.GetBySteamAsync(null), Is.Null);
        Assert.That(await _registry.GetBySteamAsync(""), Is.Null);
    }

    [Test]
    public async Task UnmarkAsync_RemovesTheMark()
    {
        await _registry.MarkAsync(Mark("steam_1"));

        await _registry.UnmarkAsync("steam_1");

        Assert.That(await _registry.GetBySteamAsync("steam_1"), Is.Null);
    }

    [Test]
    public async Task GetBySteam_ExpiredMark_SelfHealsAndReturnsNull()
    {
        await _registry.MarkAsync(Mark("steam_1", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var mark = await _registry.GetBySteamAsync("steam_1");

        Assert.That(mark, Is.Null);
        Assert.That(await _registry.IsMarkedAsync("steam_1"), Is.False, "the expired mark should have been swept");
    }

    [Test]
    public async Task GetAllAsync_ExcludesExpiredMarks()
    {
        await _registry.MarkAsync(Mark("steam_live"));
        await _registry.MarkAsync(Mark("steam_expired", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var all = await _registry.GetAllAsync();

        Assert.That(all.Select(m => m.SteamId), Is.EqualTo(new[] { "steam_live" }));
    }

    [Test]
    public async Task IsMarkedAsync_TrueOnlyWhileLive()
    {
        await _registry.MarkAsync(Mark("steam_1"));

        Assert.That(await _registry.IsMarkedAsync("steam_1"), Is.True);

        await _registry.UnmarkAsync("steam_1");

        Assert.That(await _registry.IsMarkedAsync("steam_1"), Is.False);
    }

    [Test]
    public async Task MarkAsync_MarkingAnAlreadyMarkedSteamId_OverwritesTheOldMark()
    {
        await _registry.MarkAsync(Mark("steam_1") with { Species = "Rex" });

        await _registry.MarkAsync(Mark("steam_1") with { Species = "Carno" });

        var mark = await _registry.GetBySteamAsync("steam_1");
        Assert.That(mark!.Species, Is.EqualTo("Carno"));
    }
}
