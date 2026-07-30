using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class UserJoinedHandlerTests
{
    [Test]
    public void Handle_DoesNotThrow()
    {
        var handler = new UserJoinedHandler();

        Assert.DoesNotThrow(() => handler.Handle(new UserJoinedIsleServerEvent { SteamId = "steam-1" }));
    }
}
