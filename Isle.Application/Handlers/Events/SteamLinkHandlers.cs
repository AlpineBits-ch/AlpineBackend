using Identity.Contracts.Bus.Events;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Events;

public class SteamLinkHandler(MicroserviceContext context, ILogger<SteamLinkHandler> logger)
{
    public async Task Handle(SteamLinkedEvent @event)
    {

        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.LinkUserId(@event.SteamId);
        
    }
    public async Task Handle(SteamUnlinkedEvent @event)
    {
        var player = await context.Players.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        player?.UnlinkUserId();
    }
}