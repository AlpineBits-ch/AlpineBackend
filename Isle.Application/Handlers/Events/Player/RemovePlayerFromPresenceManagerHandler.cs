using Isle.Api.Services;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Events.Player;

public class RemovePlayerFromPresenceManagerHandler
{
    public void Handle(PlayerDisconnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        playerPresenceManager.RemovePlayerId(@event.PlayerId);
    }
}