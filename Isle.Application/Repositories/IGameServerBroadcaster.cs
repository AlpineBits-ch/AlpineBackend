using System.Numerics;

namespace Isle.Api.Repositories;

public interface IGameServerBroadcaster
{
    Task TeleportPlayerAsync(Guid playerId, Vector3 position);
    Task BroadcastAsync(string message);
}