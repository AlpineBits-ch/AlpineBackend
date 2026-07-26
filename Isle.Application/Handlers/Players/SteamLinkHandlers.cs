using Identity.Contracts.Bus.Events;
using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

public class SteamLinkHandler(MicroserviceContext context, ILogger<SteamLinkHandler> logger)
{
    public async Task<CreatePlayerCommand?> Handle(SteamLinkedEvent @event)
    {

        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.LinkUserId(@event.UserId);

        if (player is null)
        {
            // we just issue a register command to register the player right away with the steam and userId
            return new CreatePlayerCommand()
            {
                SteamId = @event.SteamId,
                UserId = @event.UserId
            };
        }

        return null;
    }
    public async Task Handle(SteamUnlinkedEvent @event)
    {
        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.UnlinkUserId();
    }
}