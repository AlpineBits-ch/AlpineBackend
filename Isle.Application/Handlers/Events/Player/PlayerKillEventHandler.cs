using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Events.Player;

public class PlayerKillEventHandler
{
    public static void Handle(PlayerKillEvent @event, ILogger<PlayerKillEventHandler> logger)
    {
        logger.LogInformation("Player {PlayerId} killed {KilledPlayerId}", @event.KilerId, @event.VictimId);
    }
}