using Isle.Api.Handlers.Games;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;

namespace Isle.Tests.Tests.Handlers.Games;

[TestFixture]
public class UpdatePlayerPositionForGameModesHandlerTests
{
    [Test]
    public async Task Handle_UpdatesTheCacheWithTheReportedPosition()
    {
        var cache = new PlayerPositionCache();

        UpdatePlayerPositionForGameModesHandler.Handle(
            new UpdatePlayerPositionCommand("player-1", 10f, 20f, 30f, 45f), cache);

        var positions = await cache.GetPlayerPositionsAsync();
        Assert.That(positions, Has.Count.EqualTo(1));
        Assert.That(positions[0].PlayerId, Is.EqualTo("player-1"));
        Assert.That(positions[0].Position.X, Is.EqualTo(10f));
        Assert.That(positions[0].Position.Y, Is.EqualTo(20f));
        Assert.That(positions[0].Position.Z, Is.EqualTo(30f));
        Assert.That(positions[0].Yaw, Is.EqualTo(45f));
    }

    [Test]
    public async Task Handle_SamePlayerReportedTwice_OverwritesThePreviousPosition()
    {
        var cache = new PlayerPositionCache();

        UpdatePlayerPositionForGameModesHandler.Handle(new UpdatePlayerPositionCommand("player-1", 0f, 0f, 0f), cache);
        UpdatePlayerPositionForGameModesHandler.Handle(new UpdatePlayerPositionCommand("player-1", 5f, 5f, 5f), cache);

        var positions = await cache.GetPlayerPositionsAsync();
        Assert.That(positions, Has.Count.EqualTo(1));
        Assert.That(positions[0].Position.X, Is.EqualTo(5f));
    }
}
