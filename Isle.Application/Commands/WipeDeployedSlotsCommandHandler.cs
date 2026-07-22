using Isle.Contracts.Commands;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Commands;

public class WipeDeployedSlotsCommandHandler(MicroserviceContext context, ILogger<WipeDeployedSlotsCommandHandler> logger)
{
    public async Task Handle(WipeDeployedSlotsCommand command)
    {
        var player = await context.Players
            .Include(p => p.Storage)
            .ThenInclude(s => s.Slots)
            .FirstOrDefaultAsync(p => p.SteamId == command.SteamId);

        if (player?.Storage is null)
        {
            return;
        }

        var wiped = player.Storage.WipeDeployed();
        if (wiped == 0)
        {
            return;
        }

        await context.SaveChangesAsync();
        logger.LogInformation("Wiped {Count} deployed storage slot(s) for {Steam} after death", wiped, command.SteamId);
    }
}
