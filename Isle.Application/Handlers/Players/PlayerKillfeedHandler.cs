using Isle.Contracts.Events.Player;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Players;

/// <summary>
/// Resolves both sides of a raw killfeed line to domain player ids and republishes the resolved
/// <see cref="PlayerKillEvent"/>.
/// </summary>
public class PlayerKillfeedHandler
{
    public static async Task<PlayerKillEvent?> Handle(
        PlayerKillfeedReportedEvent @event,
        MicroserviceContext context,
        ILogger<PlayerKillfeedHandler> logger,
        CancellationToken ct)
    {
        var killerId = await ResolveAsync(context, @event.KillerSteamId, ct);
        var victimId = await ResolveAsync(context, @event.VictimSteamId, ct);

        if (killerId is null || victimId is null)
        {
            logger.LogDebug("Dropping killfeed line: killer {Killer} or victim {Victim} is not a registered player",
                @event.KillerSteamId, @event.VictimSteamId);
            return null;
        }

        return new PlayerKillEvent
        {
            KilerId = killerId,
            VictimId = victimId,
            VictimWeightInKg = @event.VictimWeightInKg,
        };
    }

    private static async Task<string?> ResolveAsync(MicroserviceContext context, string steamId, CancellationToken ct) =>
        (await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.SteamId == steamId, ct))?.Id;
}
