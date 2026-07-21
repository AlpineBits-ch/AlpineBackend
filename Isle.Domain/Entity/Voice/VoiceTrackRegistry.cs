using System.Collections.Concurrent;

namespace Isle.Api.Voice;

/// <summary>
/// Tracks, per player, the Cloudflare SFU session and published audio track that peers need in
/// order to pull that player's voice as a remote track.
/// </summary>
public sealed class VoiceTrackRegistry
{
    public readonly record struct PublishedTrack(string CfSessionId, string TrackName);

    private readonly ConcurrentDictionary<string, PublishedTrack> _tracks = new(); // playerId -> track

    /// <summary>Records the CF session + audio track a player has published (overwrites any prior entry).</summary>
    public void Publish(string playerId, string cfSessionId, string trackName) =>
        _tracks[playerId] = new PublishedTrack(cfSessionId, trackName);

    /// <summary>Drops a player's published track (on close, leave, or disconnect).</summary>
    public void Remove(string playerId) => _tracks.TryRemove(playerId, out _);

    /// <summary>True when the player has an audio track peers can subscribe to.</summary>
    public bool TryGet(string playerId, out PublishedTrack track) =>
        _tracks.TryGetValue(playerId, out track);
}
