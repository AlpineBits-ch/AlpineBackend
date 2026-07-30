using Isle.Api.Handlers.Integration;
using Isle.Domain.Events.Player;

namespace Isle.Tests.Tests.Handlers.Integration;

[TestFixture]
public class PlayerCreatedEventHandlerTests
{
    [Test]
    public void Handle_MapsDomainEventFieldsOntoTheIntegrationEvent()
    {
        var handler = new PlayerCreatedEventHandler();
        var domainEvent = new PlayerCreated { PlayerId = "player-1", SteamId = "steam-1", UserId = "usr-1" };

        var result = handler.Handle(domainEvent);

        Assert.That(result.Id, Is.EqualTo("player-1"));
        Assert.That(result.SteamId, Is.EqualTo("steam-1"));
        Assert.That(result.UserId, Is.EqualTo("usr-1"));
    }

    [Test]
    public void Handle_NoLinkedUser_MapsNullUserId()
    {
        var handler = new PlayerCreatedEventHandler();
        var domainEvent = new PlayerCreated { PlayerId = "player-1", SteamId = "steam-1", UserId = null };

        var result = handler.Handle(domainEvent);

        Assert.That(result.UserId, Is.Null);
    }
}
