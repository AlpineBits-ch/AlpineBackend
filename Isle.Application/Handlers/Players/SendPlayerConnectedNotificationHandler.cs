using Echo.Realtime;
using Isle.Contracts.Events.Player;
using Microsoft.AspNetCore.SignalR;

namespace Isle.Api.Handlers.Players;

/// <summary>
/// Pushes an <c>isle.PlayerJoined</c> socket message to the account linked to a player who just
/// connected to the game server.
/// </summary>
public class SendPlayerConnectedNotificationHandler(IHubContext<EchoRealtimeHub> hubContext,
    ILogger<SendPlayerConnectedNotificationHandler> logger)
{
    public async Task Handle(PlayerConnectedEvent @event)
    {
        if (string.IsNullOrWhiteSpace(@event.UserId))
        {
            logger.LogDebug("Player {PlayerId} joined with no linked account - nowhere to route the socket message",
                @event.PlayerId);
            return;
        }

        await hubContext.Clients.User(@event.UserId).SendAsync("isle.PlayerJoined", new
        {
            playerId = @event.PlayerId,
            steamId = @event.SteamId,
            userId = @event.UserId,
        });
    }
}
