using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Events.IsleUser;

public class CreatePlayerIfNotExistsHandler
{
    public async Task<CreatePlayerCommand?> Handle(UserJoinedIsleServerEvent @event, MicroserviceContext context)
    {
        var existingPlayer = context.Players.AsNoTracking().FirstOrDefault(x => x.SteamId == @event.SteamId);
        if(existingPlayer is null)
        {
            return new CreatePlayerCommand()
            {
                SteamId = @event.SteamId,
            };
        }
        return null;
    }
}