using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Integration;

/// <summary>
/// Isle's participant in the AccountDeletionSaga fan-out (T1-9 of docs/specs/privacy.md).
/// </summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(
        PurgeUserDataCommand command,
        MicroserviceContext ctx,
        VoicePlayerRegistry registry,
        VoiceTrackRegistry tracks,
        VoiceCluster cluster,
        ILogger<PurgeUserDataCommandHandler> logger)
    {
        var players = await ctx.Players
            .Where(p => p.UserId == command.UserId)
            .ToListAsync();

        foreach (var player in players)
        {
            player.UnlinkUserId();
        }

        // Removing from the grid also tells everyone currently within earshot that the player is
        // gone, so no peer is left holding a subscription to a track that will never update again.
        cluster.RemovePlayer(command.UserId);
        tracks.Remove(command.UserId);
        await registry.UnregisterAsync(command.UserId);

        if (players.Count > 0)
        {
            logger.LogInformation(
                "Purge: unlinked {Count} Isle player record(s) from account {UserId} and dropped its "
                + "positional/voice state", players.Count, command.UserId);
        }

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "isle",
        };
    }
}
