namespace Echo.Voice.Usage;

/// <summary>The media kinds usage is broken down by.</summary>
public enum VoiceUsageTrackKind
{
    /// <summary>A publisher's microphone.</summary>
    Audio,

    /// <summary>A publisher's camera.</summary>
    Camera,

    /// <summary>The video half of a screen share.</summary>
    Screen,

    /// <summary>The shared application's own sound, distinct from the publisher's microphone.</summary>
    ScreenAudio,
}

/// <summary>Storage names for <see cref="VoiceUsageTrackKind"/>.</summary>
public static class VoiceUsageTrackKinds
{
    public const string Audio = "audio";
    public const string Camera = "camera";
    public const string Screen = "screen";
    public const string ScreenAudio = "screenAudio";

    /// <summary>Every kind, in a fixed order, so a read of a day hash always produces the same
    /// shape whether or not a given kind was used that day.</summary>
    public static readonly IReadOnlyList<VoiceUsageTrackKind> All =
    [
        VoiceUsageTrackKind.Audio,
        VoiceUsageTrackKind.Camera,
        VoiceUsageTrackKind.Screen,
        VoiceUsageTrackKind.ScreenAudio,
    ];

    public static string Name(VoiceUsageTrackKind kind) => kind switch
    {
        VoiceUsageTrackKind.Audio => Audio,
        VoiceUsageTrackKind.Camera => Camera,
        VoiceUsageTrackKind.Screen => Screen,
        VoiceUsageTrackKind.ScreenAudio => ScreenAudio,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static VoiceUsageTrackKind? Parse(string name) => name switch
    {
        Audio => VoiceUsageTrackKind.Audio,
        Camera => VoiceUsageTrackKind.Camera,
        Screen => VoiceUsageTrackKind.Screen,
        ScreenAudio => VoiceUsageTrackKind.ScreenAudio,
        _ => null,
    };

    /// <summary>
    /// Maps a <see cref="Echo.Voice.Tracks.TrackDescriptor.Kind"/> onto a usage kind.
    /// </summary>
    public static VoiceUsageTrackKind FromTrackKind(string trackKind) => trackKind switch
    {
        Tracks.TrackNaming.AudioKind => VoiceUsageTrackKind.Audio,
        Tracks.TrackNaming.ScreenKind => VoiceUsageTrackKind.Screen,
        Tracks.TrackNaming.ScreenAudioKind => VoiceUsageTrackKind.ScreenAudio,
        _ => VoiceUsageTrackKind.Camera,
    };
}
