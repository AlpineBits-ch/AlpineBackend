using Isle.Api.Services;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Events.Player;

public class RemovePlayerFromPresenceManagerHandler
{
    public async Task Handle(PlayerDisconnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        await playerPresenceManager.RemovePlayerIdAsync(@event.PlayerId);
    }
}