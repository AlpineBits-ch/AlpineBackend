using Bots.Infrastructure.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Bots.Application.Commands;

/// <summary>
/// Bots' participant in the AccountDeletionSaga fan-out (T1-9 of docs/specs/privacy.md).
/// </summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(
        PurgeUserDataCommand command,
        MicroserviceContext ctx,
        IMessageBus bus,
        ILogger<PurgeUserDataCommandHandler> logger)
    {
        var owned = await ctx.BotApplications
            .Where(a => a.OwnerUserId == command.UserId)
            .ToListAsync();

        if (owned.Count == 0)
        {
            return new PurgeUserDataCommandResponse { UserId = command.UserId, Service = "bots" };
        }

        var applicationIds = owned.Select(a => a.Id).ToList();

        var installations = await ctx.BotInstallations
            .Where(i => applicationIds.Contains(i.BotApplicationId))
            .ToListAsync();
        ctx.BotInstallations.RemoveRange(installations);

        foreach (var app in owned)
        {
            // Only for applications still enabled: DisableBotAccountCommand is idempotent, but not
            // re-sending it keeps a redelivered purge from putting another N messages on the bus.
            if (app.IsEnabled)
            {
                await bus.InvokeAsync(new DisableBotAccountCommand { BotUserId = app.BotUserId });
                app.IsEnabled = false;
            }

            // The owner id is deliberately left pointing at the tombstoned account rather than
            // nulled.
        }

        logger.LogInformation(
            "Purge: disabled {Applications} bot application(s) and removed {Installations} installation(s) "
            + "owned by {UserId}", owned.Count, installations.Count, command.UserId);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "bots",
        };
    }
}
