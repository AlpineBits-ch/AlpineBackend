using Isle.Api.Services;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Events.Player;

public class AddPlayerToPresenceManagerHandler
{
    public async Task Handle(PlayerConnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        await playerPresenceManager.AddPlayerIdAsync(@event.PlayerId);
    }
}