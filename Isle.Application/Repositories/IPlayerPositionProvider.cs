using System.Numerics;

namespace Isle.Api.Repositories;
public record PlayerPosition(string PlayerId, Vector3 Position, float Yaw, DateTime UpdatedAt);
public interface IPlayerPositionProvider
{
    Task<IReadOnlyList<PlayerPosition>> GetPlayerPositionsAsync();

}