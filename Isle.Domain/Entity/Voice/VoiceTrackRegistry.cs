using System.Collections.Concurrent;

namespace Isle.Domain.Entity.Voice;

/// <summary>
/// Tracks, per player, the Cloudflare SFU session and published audio track that peers need in
/// order to pull that player's voice as a remote track.
/// </summary>
public sealed class VoiceTrackRegistry
{
    public readonly record struct PublishedTrack(string CfSessionId, string TrackName);

    private readonly ConcurrentDictionary<string, PublishedTrack> _tracks = new(); // playerId -> track

    // cfSessionId -> playerId, for every session this server has minted.
    private readonly ConcurrentDictionary<string, string> _sessionOwners = new();

    /// <summary>Records that <paramref name="playerId"/> minted <paramref name="cfSessionId"/>.</summary>
    public void RegisterSession(string playerId, string cfSessionId) =>
        _sessionOwners[cfSessionId] = playerId;

    /// <summary>True when <paramref name="cfSessionId"/> was minted by <paramref name="playerId"/>.</summary>
    public bool OwnsSession(string playerId, string? cfSessionId) =>
        !string.IsNullOrWhiteSpace(cfSessionId)
        && _sessionOwners.TryGetValue(cfSessionId, out var owner)
        && owner == playerId;

    /// <summary>The player behind a session id, or null if this server did not mint it.</summary>
    public string? ResolveSessionOwner(string? cfSessionId) =>
        !string.IsNullOrWhiteSpace(cfSessionId) && _sessionOwners.TryGetValue(cfSessionId, out var owner)
            ? owner
            : null;

    /// <summary>Records the CF session + audio track a player has published (overwrites any prior entry).</summary>
    public void Publish(string playerId, string cfSessionId, string trackName)
    {
        _tracks[playerId] = new PublishedTrack(cfSessionId, trackName);
        RegisterSession(playerId, cfSessionId);
    }

    /// <summary>Drops a player's published track (on close, leave, or disconnect).</summary>
    public void Remove(string playerId)
    {
        _tracks.TryRemove(playerId, out _);

        // Drop the player's sessions too, so a stale id cannot be pulled after they leave.
        foreach (var (sessionId, owner) in _sessionOwners)
        {
            if (owner == playerId) _sessionOwners.TryRemove(sessionId, out _);
        }
    }

    /// <summary>True when the player has an audio track peers can subscribe to.</summary>
    public bool TryGet(string playerId, out PublishedTrack track) =>
        _tracks.TryGetValue(playerId, out track);
}
