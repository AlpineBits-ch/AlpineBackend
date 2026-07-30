using Isle.Api.Handlers.Players;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class AddPlayerToPresenceManagerHandlerTests
{
    private PlayerPresenceManager _presence = null!;
    private AddPlayerToPresenceManagerHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _handler = new AddPlayerToPresenceManagerHandler();
    }

    [Test]
    public async Task Handle_MarksThePlayerOnline()
    {
        Assert.That(_presence.IsPlayerOnline("player-1"), Is.False);

        await _handler.Handle(new PlayerConnectedEvent { PlayerId = "player-1", SteamId = "steam-1" }, _presence);

        Assert.That(_presence.IsPlayerOnline("player-1"), Is.True);
    }
}
