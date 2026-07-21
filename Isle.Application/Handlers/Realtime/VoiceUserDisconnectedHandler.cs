using Echo.Realtime;
using Isle.Api.Services;
using Isle.Api.Voice;
using Isle.Contracts.Commands;
using Wolverine;

namespace Isle.Api.Handlers.Realtime;

/// <summary>
/// A hub disconnect (e.g. the player closes the browser tab) must also remove them from the
/// proximity voice grid. Otherwise the only cleanup paths are in-game leave and the explicit
/// <c>POST /voice/leave</c>, so a dropped socket leaves the player frozen in-grid and peers keep
/// hearing them at their last position. Mirrors <c>VoiceMembershipEndpoints.Leave</c>.
///
/// <para><c>UserDisconnected</c> is fanned out (RabbitMQ conventional routing) to every service
/// that handles it; this handler fires for all disconnects, so it no-ops for anyone who wasn't
/// an Isle voice participant.</para>
/// </summary>
public class VoiceUserDisconnectedHandler
{
    public static async Task Handle(
        UserDisconnected message,
        VoicePlayerRegistry registry,
        VoiceTrackRegistry tracks,
        IMessageBus bus)
    {
        // Skip chat-only / non-voice users — nothing to tear down.
        if (!registry.TryGetSteamId(message.UserId, out _))
            return;

        registry.Unregister(message.UserId);
        tracks.Remove(message.UserId);

        // Removes them from the cluster, which emits PeerBecameInaudible for every remaining
        // neighbour → isle.PeerLeft to both sides → no ghost left behind. On reconnect the
        // client re-runs /voice/join and rebuilds its proximity list (see frontend guide §6.3).
        await bus.InvokeAsync(new RemovePlayerCommand(message.UserId));
    }
}
