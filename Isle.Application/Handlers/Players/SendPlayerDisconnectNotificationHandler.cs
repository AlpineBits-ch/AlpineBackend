using Echo.Realtime;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

public class SendPlayerDisconnectNotificationHandler(IHubContext<EchoRealtimeHub> hubContext, MicroserviceContext context)
{
    public async Task Handle(PlayerDisconnectedEvent playerDisconnectedEvent)
    {
        var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerDisconnectedEvent.PlayerId);

        if (!string.IsNullOrWhiteSpace(player?.UserId))
        {
            await hubContext.Clients.User(player.UserId).SendAsync("isle.PlayerDisconnected", new {
                playerId = playerDisconnectedEvent.PlayerId,
                steamId = player.SteamId,
                userId = player.UserId
            });
        }
    }
}