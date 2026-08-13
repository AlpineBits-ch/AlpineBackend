using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Players;

/// <summary>
/// Turns the join half of the game server's connection feed into an open <see
/// cref="Isle.Domain.Entity.PlaySession"/>.
/// </summary>
public class OpenPlaySessionOnConnectHandler(PlaySessionTracker sessions, WorldRosterCache roster)
{
    public Task Handle(PlayerConnectedEvent @event, CancellationToken ct) =>
        sessions.StartAsync(@event.PlayerId, roster.FindBySteam(@event.SteamId)?.Species, ct);
}

/// <summary>
/// Closes the session on a reported leave - the one path that produces an exact end time.
/// </summary>
public class ClosePlaySessionOnDisconnectHandler(PlaySessionTracker sessions)
{
    public Task Handle(PlayerDisconnectedEvent @event, CancellationToken ct) =>
        sessions.EndAsync(@event.PlayerId, ct);
}
