using System.Collections.Concurrent;
using System.Numerics;
using Isle.Api.Repositories;

namespace Isle.Api.Services;

public class PlayerPositionCache : IPlayerPositionProvider
{
    private readonly ConcurrentDictionary<string, PlayerPosition> _positions = new();

    public void Update(string playerId, Vector3 position, float yaw)
    {
        _positions[playerId] = new PlayerPosition(playerId, position, yaw, DateTime.UtcNow);
    }

    public Task<IReadOnlyList<PlayerPosition>> GetPlayerPositionsAsync()
    {
        return Task.FromResult<IReadOnlyList<PlayerPosition>>(_positions.Values.ToList());
    }
}