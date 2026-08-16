using System.Collections.Concurrent;

namespace Isle.Domain.Entity.Voice;

/// <summary>
/// Which players are currently publishing into the proximity room, and the track handle peers need
/// in order to hear them.
/// </summary>
public sealed class VoiceTrackRegistry
{
    /// <param name="Identity">The player's participant identity at the SFU.</param>
    /// <param name="TrackSid">The SFU's own id for the published track.</param>
    public readonly record struct PublishedTrack(string Identity, string TrackSid, string TrackName);

    private readonly ConcurrentDictionary<string, PublishedTrack> _tracks = new(); // playerId -> track

    /// <summary>Records that a player is publishing.</summary>
    public void Publish(string playerId, string identity, string trackSid, string trackName) =>
        _tracks[playerId] = new PublishedTrack(identity, trackSid, trackName);

    /// <summary>Drops a player's published track (on leave, or disconnect).</summary>
    public void Remove(string playerId) => _tracks.TryRemove(playerId, out _);

    /// <summary>True when the player has a track peers can subscribe to.</summary>
    public bool TryGet(string playerId, out PublishedTrack track) =>
        _tracks.TryGetValue(playerId, out track);

    /// <summary>Everyone currently publishing.</summary>
    public IReadOnlyCollection<string> Publishers => _tracks.Keys.ToList();

    /// <summary>Replaces the whole map with what the SFU reports.</summary>
    public void Sync(IEnumerable<(string PlayerId, PublishedTrack Track)> live)
    {
        var fresh = live.ToDictionary(e => e.PlayerId, e => e.Track, StringComparer.Ordinal);

        foreach (var playerId in _tracks.Keys.Where(k => !fresh.ContainsKey(k)).ToList())
            _tracks.TryRemove(playerId, out _);

        foreach (var (playerId, track) in fresh)
            _tracks[playerId] = track;
    }
}
