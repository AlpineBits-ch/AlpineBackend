using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

public class ChangePlayerAdminStatusCommandHandler(MicroserviceContext context, ILogger<ChangePlayerAdminStatusCommandHandler> logger)
{
    public async Task Handle(ChangePlayerAdminStatusCommand command)
    {
        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == command.PlayerId);
        if (player is null)
        {
            logger.LogWarning("Player with id {PlayerId} not found", command.PlayerId);
            return;
        }
        
        if(command.IsAdmin)
        {
            player.SetAdmin();
        }
        else
        {
            player.UnsetAdmin();
        }
    }
}