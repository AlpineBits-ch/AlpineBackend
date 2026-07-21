using System.Collections.Concurrent;

namespace Isle.Api.Services;

public class PlayerPresenceManager
{
    private readonly ConcurrentDictionary<string, byte> _playerIds = new();


    public void AddPlayerId(string playerId)
    {
        _playerIds.TryAdd(playerId, 0);
        
    }
    
    public void RemovePlayerId(string playerId)
    {
        _playerIds.TryRemove(playerId, out _);
        
    }
    
    public bool IsPlayerOnline(string playerId)
    {
        return _playerIds.ContainsKey(playerId);
    }

    public void Clear()
    {
        _playerIds.Clear();
    }
}