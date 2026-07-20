using Identity.Contracts.Bus.Events;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Events;

public class SteamLinkHandlers(MicroserviceContext context)
{
    public async Task HandleSteamLinkedEvent(SteamLinkedEvent @event)
    {

        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.LinkUserId(@event.SteamId);
    }
    public async Task HandleSteamUnlinkedEvent(SteamUnlinkedEvent @event)
    {
        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.UnlinkUserId();
    }
}