using Isle.Contracts.Events;
using Isle.Domain.Events.Player;

namespace Isle.Api.Integration;

public class PlayerCreatedEventHandler
{
    public PlayerCreatedEvent Handle(PlayerCreated playerCreated)
    {
        return new PlayerCreatedEvent()
        {
            Id = playerCreated.Id,
            SteamId = playerCreated.SteamId,
            UserId = playerCreated.UserId
        };
    }
}