using Isle.Api.Services;
using Isle.Contracts.Events.Player;
using Isle.Api.Services.State;

namespace Isle.Api.Handlers.Players;

public class AddPlayerToPresenceManagerHandler
{
    public async Task Handle(PlayerConnectedEvent @event, PlayerPresenceManager playerPresenceManager)
    {
        await playerPresenceManager.AddPlayerIdAsync(@event.PlayerId);
    }
}