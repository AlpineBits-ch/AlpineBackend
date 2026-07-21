using Echo.Realtime;
using Isle.Api.Services;
using Isle.Api.Voice;
using Isle.Contracts.Commands;
using Wolverine;

namespace Isle.Api.Handlers.Realtime;

/// <summary>
/// A hub disconnect (e.g. the player closes the browser tab) must also remove them from the
/// proximity voice grid.
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
        // neighbour → isle.PeerLeft to both sides → no ghost left behind.
        await bus.InvokeAsync(new RemovePlayerCommand(message.UserId));
    }
}
