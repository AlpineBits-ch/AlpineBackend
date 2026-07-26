using Isle.Api.Services;
using Isle.Contracts.Events.Player;
using Isle.Api.Services.State;

namespace Isle.Api.Handlers.Players;

public class RemovePlayerFromPresenceManagerHandler
{
    public async Task Handle(PlayerDisconnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        await playerPresenceManager.RemovePlayerIdAsync(@event.PlayerId);
    }
}