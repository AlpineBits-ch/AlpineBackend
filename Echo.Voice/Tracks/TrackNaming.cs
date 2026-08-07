namespace Echo.Voice.Tracks;

/// <summary>
/// What a track name means: the kind of media it carries, and the screen share it belongs to when
/// it is part of one.
/// </summary>
/// <param name="TrackName">The name as published to the SFU, unchanged.</param>
/// <param name="Kind">One of <c>audio</c>, <c>video</c>, <c>screen</c>, <c>screenAudio</c>.</param>
/// <param name="ShareId">
/// The screen share this track belongs to, or null for microphone and camera tracks.
/// </param>
public readonly record struct TrackDescriptor(string TrackName, string Kind, string? ShareId);

/// <summary>The one place that knows how a track name is built and taken apart.</summary>
public static class TrackNaming
{
    /// <summary>The microphone track.</summary>
    public const string Audio = "audio";

    public const string ScreenPrefix = "screen-";
    public const string ScreenAudioPrefix = "screen-audio-";

    public const string AudioKind = "audio";
    public const string VideoKind = "video";
    public const string ScreenKind = "screen";
    public const string ScreenAudioKind = "screenAudio";

    /// <summary>The video track name for a screen share.</summary>
    public static string ScreenTrack(string shareId) => ScreenPrefix + shareId;

    /// <summary>The audio track name for a screen share (the shared tab or application's own
    /// sound, distinct from the publisher's microphone).</summary>
    public static string ScreenAudioTrack(string shareId) => ScreenAudioPrefix + shareId;

    /// <summary>Classifies a track name.</summary>
    public static TrackDescriptor Describe(string trackName)
    {
        // Order matters - see the class remarks.
        if (trackName.StartsWith(ScreenAudioPrefix, StringComparison.Ordinal))
            return new TrackDescriptor(trackName, ScreenAudioKind, trackName[ScreenAudioPrefix.Length..]);

        if (trackName.StartsWith(ScreenPrefix, StringComparison.Ordinal))
            return new TrackDescriptor(trackName, ScreenKind, trackName[ScreenPrefix.Length..]);

        if (string.Equals(trackName, Audio, StringComparison.Ordinal))
            return new TrackDescriptor(trackName, AudioKind, null);

        return new TrackDescriptor(trackName, VideoKind, null);
    }

    /// <summary>Whether this name is the publisher's microphone.</summary>
    public static bool IsMicrophone(string? trackName) =>
        string.Equals(trackName, Audio, StringComparison.Ordinal);

    /// <summary>Whether this track belongs to a screen share, in either of its two halves.</summary>
    public static bool IsScreenShare(string trackName) =>
        trackName.StartsWith(ScreenPrefix, StringComparison.Ordinal);
}
