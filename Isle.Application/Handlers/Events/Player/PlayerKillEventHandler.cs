using Isle.Contracts.Events.Player;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;

namespace Isle.Api.Handlers.Events.Player;

public class PlayerKillEventHandler
{
    public static async Task Handle(PlayerKillEvent @event, ILogger<PlayerKillEventHandler> logger, MicroserviceContext context)
    {
        logger.LogInformation("Player {PlayerId} killed {KilledPlayerId}", @event.KilerId, @event.VictimId);


        var date = DateTime.UtcNow;
        await context.KillLogs.AddAsync(new KillLog()
        {
            Id = KillLog.GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            KillerId = @event.KilerId,
            VictimId = @event.VictimId,
            VictimWeightKg = @event.VictimWeightInKg,
        });
    }
}