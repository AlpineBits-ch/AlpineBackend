using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

public class WipeDeployedSlotsCommandHandler(MicroserviceContext context, ILogger<WipeDeployedSlotsCommandHandler> logger)
{
    /// <summary>
    /// A dino tied to a deployed storage slot just died — issue the wipe so the slot frees up for a
    /// new dino.
    /// </summary>
    public static WipeDeployedSlotsCommand Handle(UserDiedOnIsleServerEvent @event) =>
        new() { SteamId = @event.SteamId };

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

        // No manual SaveChangesAsync here: Wolverine's AutoApplyTransactions/UseEntityFrameworkCoreTransactions
        // middleware already commits this tracked DbContext after the handler returns. Calling it again here
        // double-saves against the same context (this exact pattern caused a production incident before).
        logger.LogInformation("Wiped {Count} deployed storage slot(s) for {Steam} after death", wiped, command.SteamId);
    }
}
