using Echo.Realtime;
using Isle.Api;
using Isle.Api.Services;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Handlers.Realtime;

/// <summary>
/// A realtime socket drop (tab close, app restart, network blip with SignalR auto-reconnect) is
/// <b>not</b> the same as leaving voice. The player's in-game character is the source of truth for
/// grid membership — kept fresh by the stats stream, and torn down by the game <c>Leave</c> event,
/// an explicit <c>POST /voice/leave</c>, or the 2h registry TTL reconcile. So we deliberately keep
/// the player in <see cref="VoicePlayerRegistry"/> and <see cref="VoiceCluster"/> across a drop:
/// their cell (and last position/velocity) stays warm, which is exactly what lets a reconnect
/// re-seed peers instantly (see <c>VoiceCloudflareEndpoints.TracksNew</c>) instead of the old
/// "0 nearby players until you move and rejoin" behaviour.
///
/// <para>What a drop <i>does</i> invalidate is the live media: the Cloudflare session/track dies
/// with the client. So we drop the published track and tell current neighbours to stop pulling it,
/// so nobody keeps a dead track open. On reconnect the client republishes and everyone re-subscribes.</para>
///
/// <para><c>UserDisconnected</c> is fanned out to every service that handles it; this no-ops for
/// anyone who wasn't an Isle voice participant / wasn't publishing.</para>
/// </summary>
public class VoiceUserDisconnectedHandler
{
    public static async Task Handle(
        UserDisconnected message,
        VoicePlayerRegistry registry,
        VoiceTrackRegistry tracks,
        VoiceCluster cluster,
        ISfuClient sfu)
    {
        // Skip chat-only / non-voice users — nothing to tear down.
        if (!registry.TryGetSteamId(message.UserId, out _))
            return;

        // Not publishing (no live media to invalidate) — leave grid + registry untouched.
        if (!tracks.TryGet(message.UserId, out _))
            return;

        tracks.Remove(message.UserId);

        // Tell everyone who could hear this player to drop the now-dead Cloudflare track.
        // UnsubscribePair is symmetric; the message back to the (offline) mover is a harmless no-op.
        foreach (var peer in cluster.GetAudiblePeers(message.UserId).Where(p => p != message.UserId))
            await sfu.UnsubscribePair(message.UserId, peer);
    }
}
