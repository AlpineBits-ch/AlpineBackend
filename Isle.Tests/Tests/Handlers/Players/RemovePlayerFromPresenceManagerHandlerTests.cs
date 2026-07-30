using Isle.Api.Handlers.Players;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class RemovePlayerFromPresenceManagerHandlerTests
{
    private PlayerPresenceManager _presence = null!;
    private RemovePlayerFromPresenceManagerHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _handler = new RemovePlayerFromPresenceManagerHandler();
    }

    [Test]
    public async Task Handle_KnownOnlinePlayer_MarksThemOffline()
    {
        await _presence.AddPlayerIdAsync("player-1");
        Assert.That(_presence.IsPlayerOnline("player-1"), Is.True);

        await _handler.Handle(new PlayerDisconnectedEvent { PlayerId = "player-1", SteamId = "steam-1" }, _presence);

        Assert.That(_presence.IsPlayerOnline("player-1"), Is.False);
    }

    [Test]
    public void Handle_PlayerNeverMarkedOnline_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _handler.Handle(
            new PlayerDisconnectedEvent { PlayerId = "player-missing", SteamId = "steam-missing" }, _presence));
    }
}
