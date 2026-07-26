using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

public class UserLeftHandler(MicroserviceContext context)
{
    public async Task<PlayerDisconnectedEvent?> Handle(UserLeftIsleServerEvent userLeftEvent)
    {
        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == userLeftEvent.SteamId);
        if (player == null)
        {
            return null;
        }

        return new PlayerDisconnectedEvent()
        {
            PlayerId = player.Id,
            SteamId = player.SteamId,

        };
    }
}