namespace Echo.Voice.Usage;

/// <summary>
/// The bitrate assumptions that turn measured subscriber-seconds into an egress estimate.
/// </summary>
public static class VoiceUsageRates
{
    public const int AudioKilobitsPerSecond = 32;
    public const int CameraKilobitsPerSecond = 1500;
    public const int ScreenKilobitsPerSecond = 2500;
    public const int ScreenAudioKilobitsPerSecond = 48;

    public static int KilobitsPerSecond(VoiceUsageTrackKind kind) => kind switch
    {
        VoiceUsageTrackKind.Audio => AudioKilobitsPerSecond,
        VoiceUsageTrackKind.Camera => CameraKilobitsPerSecond,
        VoiceUsageTrackKind.Screen => ScreenKilobitsPerSecond,
        VoiceUsageTrackKind.ScreenAudio => ScreenAudioKilobitsPerSecond,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Estimated bytes leaving the SFU for one kind's accumulated subscriber-seconds.</summary>
    public static long EgressBytes(VoiceUsageTrackKind kind, long subscriberSeconds) =>
        subscriberSeconds * KilobitsPerSecond(kind) * 1000L / 8L;

    public static long EgressBytes(IReadOnlyDictionary<VoiceUsageTrackKind, long> subscriberSeconds) =>
        subscriberSeconds.Sum(pair => EgressBytes(pair.Key, pair.Value));
}
