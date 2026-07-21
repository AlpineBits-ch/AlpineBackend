using Isle.Api.Services;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Events.Player;

public class AddPlayerToPresenceManagerHandler
{
    public void Handle(PlayerConnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        playerPresenceManager.AddPlayerId(@event.PlayerId);
    }
}