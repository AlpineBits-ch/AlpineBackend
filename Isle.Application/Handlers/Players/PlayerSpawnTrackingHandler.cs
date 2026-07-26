using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Players;

/// <summary>Records roughly when each player last (re)entered the world.</summary>
public class PlayerSpawnTrackingHandler
{
    public static Task Handle(UserJoinedIsleServerEvent @event, PlayerSpawnTracker tracker, CancellationToken ct) =>
        tracker.MarkSpawnedAsync(@event.SteamId, ct);

    public static Task Handle(UserDiedOnIsleServerEvent @event, PlayerSpawnTracker tracker, CancellationToken ct) =>
        tracker.MarkSpawnedAsync(@event.SteamId, ct);
}
