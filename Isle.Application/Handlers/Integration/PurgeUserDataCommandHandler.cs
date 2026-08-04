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
///
/// <para><b>The Player row is not deleted, the account link is cut.</b> A player is identified by
/// Steam id, not by Echo account: the same person can keep playing on the game server after deleting
/// their Echo account, and their kill logs, quest history and storage are the game's data about a
/// Steam identity rather than personal data the Echo account owns. Deleting the row would also take
/// out <c>KillLog</c> rows describing <i>other</i> players' kills and <c>QuestInstance</c> rows other
/// people completed. So <see cref="Player.UnlinkUserId"/> is the whole write: after it, nothing in
/// this database points at the purged Echo account id.</para>
///
/// <para><b>The positional and voice state goes entirely.</b> That is the part that is unambiguously
/// personal data about the Echo account, and unlike the Player row it is keyed by the account id
/// directly (see <see cref="VoicePlayerRegistry"/> - its "playerId" is the Echo user id). Live world
/// coordinates, velocity and facing, the Steam id they map to, and the Cloudflare session/track ids
/// that let a peer pull that account's microphone are all dropped here. They are in-memory and
/// Redis-backed with a two-hour TTL, so they would have aged out on their own - but "your location
/// stops being tracked within two hours of you asking us to delete everything" is not an answer.</para>
///
/// <para>Idempotent: a redelivered command finds an already-unlinked player and empty registries, and
/// every removal below is a no-op on a key that is not there. Wolverine's transactional middleware
/// commits the context - no SaveChangesAsync here, per this repo's convention.</para>
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
        // The returned changes are not fanned out: the account is being erased, not leaving a
        // session, and the peers' own next position update reconciles them.
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
