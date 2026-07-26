using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Voice;

/// <summary>
/// Leaving the game server ends voice participation: the in-game character is the source of truth
/// for grid membership, so the registry entry goes and the player is removed from the cluster.
/// </summary>
public class VoiceLeaveOnGameLeaveHandler
{
    public static async Task<object?> Handle(UserLeftIsleServerEvent @event, VoicePlayerRegistry registry)
    {
        if (!registry.TryGetPlayerId(@event.SteamId, out var playerId))
            return null; // wasn't opted into voice — nothing to clean up

        await registry.UnregisterBySteamIdAsync(@event.SteamId);

        return new RemovePlayerCommand(playerId);
    }
}
