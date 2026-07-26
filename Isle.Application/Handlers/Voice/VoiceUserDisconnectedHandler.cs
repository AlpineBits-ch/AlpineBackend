using Echo.Realtime;
using Isle.Api.Services.State;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Handlers.Voice;

/// <summary>
/// A realtime socket drop (tab close, app restart, network blip with SignalR auto-reconnect) is not
/// the same as leaving voice.
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
        foreach (var peer in cluster.GetAudiblePeers(message.UserId).Where(p => p != message.UserId))
            await sfu.UnsubscribePair(message.UserId, peer);
    }
}
