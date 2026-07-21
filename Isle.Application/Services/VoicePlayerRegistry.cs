using System.Collections.Concurrent;

namespace Isle.Api.Services;

public sealed class VoicePlayerRegistry
{
    private readonly ConcurrentDictionary<string, string> _steamToPlayer = new(); // steamId -> playerId
    private readonly ConcurrentDictionary<string, string> _playerToSteam = new(); // playerId -> steamId

    public void Register(string playerId, string steamId)
    {
        _steamToPlayer[steamId] = playerId;
        _playerToSteam[playerId] = steamId;
    }

    public void Unregister(string playerId)
    {
        if (_playerToSteam.TryRemove(playerId, out var steamId))
            _steamToPlayer.TryRemove(steamId, out _);
    }

    public void UnregisterBySteamId(string steamId)
    {
        if (_steamToPlayer.TryRemove(steamId, out var playerId))
            _playerToSteam.TryRemove(playerId, out _);
    }

    public bool TryGetPlayerId(string steamId, out string playerId) =>
        _steamToPlayer.TryGetValue(steamId, out playerId!);

    public bool TryGetSteamId(string playerId, out string steamId) =>
        _playerToSteam.TryGetValue(playerId, out steamId!);
}