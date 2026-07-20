using Identity.Contracts.Bus.Events;
using Identity.Infrastructure.Persistence;
using Isle.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Handlers;

public class IslePlayerHandler(MicroserviceContext context)
{
    public async Task<SteamLinkedEvent?> HandleIslePlayerCreated(PlayerCreatedEvent @event)
    {
        var user = await context.Users.FirstOrDefaultAsync(p => p.SteamId == @event.SteamId);
        if (user is not null)
        {
            return new SteamLinkedEvent()
            {
                UserId = user.Id,
                SteamId = @event.SteamId,
            };
        }
        return null;
    }
}