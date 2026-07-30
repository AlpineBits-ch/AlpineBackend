using Isle.Api.Handlers.Players;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class PlayerSpawnTrackingHandlerTests
{
    private PlayerSpawnTracker _tracker = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _tracker = new PlayerSpawnTracker(cache);
    }

    [Test]
    public async Task Handle_UserJoinedEvent_RecordsASpawnTimestamp()
    {
        Assert.That(await _tracker.GetLastSpawnAsync("steam-1"), Is.Null);

        await PlayerSpawnTrackingHandler.Handle(new UserJoinedIsleServerEvent { SteamId = "steam-1" }, _tracker, CancellationToken.None);

        Assert.That(await _tracker.GetLastSpawnAsync("steam-1"), Is.Not.Null);
    }

    [Test]
    public async Task Handle_UserDiedEvent_RecordsASpawnTimestamp()
    {
        Assert.That(await _tracker.GetLastSpawnAsync("steam-1"), Is.Null);

        await PlayerSpawnTrackingHandler.Handle(new UserDiedOnIsleServerEvent { SteamId = "steam-1" }, _tracker, CancellationToken.None);

        Assert.That(await _tracker.GetLastSpawnAsync("steam-1"), Is.Not.Null);
    }
}
