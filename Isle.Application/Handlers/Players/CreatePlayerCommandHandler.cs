using Isle.Contracts.Commands;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;

namespace Isle.Api.Handlers.Players;

public class CreatePlayerCommandHandler(MicroserviceContext context)
{
    public async Task<CreatePlayerCommandResponse> Handle(CreatePlayerCommand command)
    {
        var player = Player.Create(new CreatePlayerArgs()
        {
            IsAdmin = command.IsAdmin,
            SteamId = command.SteamId,
            UserId = command.UserId,
        });
        
        context.Players.Add(player);
        
        return new CreatePlayerCommandResponse()
        {
            PlayerId = player.Id,
            Failures = [],
        };
    }
}