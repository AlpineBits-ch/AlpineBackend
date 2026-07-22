using System.Collections.Concurrent;

namespace Isle.Domain.Entity.Voice;

/// <summary>
/// Tracks, per player, the Cloudflare SFU session and published audio track that
/// peers need in order to pull that player's voice as a remote track.
///
/// In-memory and single-instance by design — it mirrors the <c>VoiceCluster</c> grid and
/// <c>VoicePlayerRegistry</c>, which already bind proximity voice to one owning process.
/// On a server restart this map is empty; clients rebuild it by re-issuing their
/// CF session/track publish after the reconnect, which re-triggers proximity subscriptions.
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
