using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Handlers.Players;

public class CreatePlayerIfNotExistsAndEmmitPlayerHandler
{
    public async Task<object?> Handle(UserJoinedIsleServerEvent @event, MicroserviceContext context, IMessageBus bus)
    {
        var existingPlayer = context.Players.AsNoTracking().FirstOrDefault(x => x.SteamId == @event.SteamId);
        
        if(existingPlayer is null)
        {
            
            await bus.InvokeAsync(new CreatePlayerCommand()
            {
                SteamId = @event.SteamId
            });

            existingPlayer = await context.Players.FirstOrDefaultAsync(x => x.SteamId == @event.SteamId);
            if (existingPlayer == null)
            {
                throw new Exception("Player not found after creation");
            }

        }
        return new PlayerConnectedEvent()
        {
            SteamId = @event.SteamId,
            UserId = existingPlayer.UserId,
            PlayerId = existingPlayer.Id
        };
    }
}