using System.Globalization;

namespace Echo.Voice.Rooms;

/// <summary>
/// Every number behind <see cref="VoiceSubscriptionPlanner"/>, in one place and none of them
/// constants.
/// </summary>
public sealed record VoiceSubscriptionOptions
{
    /// <summary>Whether a plan is computed at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether the plan is binding rather than advisory, and with it whether the usage meter is
    /// allowed to bill against it.
    /// </summary>
    public bool Enforce { get; init; }

    /// <summary>Room size at and below which everyone subscribes to everyone, unchanged.</summary>
    public int ActiveSpeakerThreshold { get; init; } = 10;

    /// <summary>How many speakers a subscriber is subscribed to once a room is above the
    /// threshold. Pins are additive on top of this, capped by
    /// <see cref="MaxPinnedPerSubscriber"/>.</summary>
    public int ActiveSpeakerCount { get; init; } = 5;

    /// <summary>
    /// Hard ceiling on the active set, including people who are talking right now.
    /// </summary>
    public int MaxActiveSpeakers { get; init; } = 8;

    /// <summary>How many participants one subscriber may pin.</summary>
    public int MaxPinnedPerSubscriber { get; init; } = 3;

    /// <summary>How long a run of speech must last before it earns a hold window.</summary>
    public TimeSpan MinimumSpeechToHold { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>How long after speech stops a participant keeps their slot.</summary>
    public TimeSpan SpeakerHoldTime { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The floor under a slot's lifetime, whatever the ranking says.</summary>
    public TimeSpan MinimumDwell { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How stale a stored selection may get before a heartbeat re-runs it.</summary>
    public TimeSpan RecomputeInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many participants may have camera or screen media distributed at once.
    /// </summary>
    public int MaxVideoPublishers { get; init; } = 8;

    /// <summary>Whether a subscriber has to ask for a screen share's audio track.</summary>
    public bool ScreenShareAudioOptIn { get; init; } = true;

    /// <summary>Rendered tile height at or below which the low simulcast layer is chosen.</summary>
    public int LowLayerMaxHeight { get; init; } = 180;

    /// <summary>Rendered tile height at or below which the medium layer is chosen.</summary>
    public int MediumLayerMaxHeight { get; init; } = 360;

    /// <summary>
    /// The layer chosen for a subscriber who has not reported a tile size in a room below the
    /// threshold.
    /// </summary>
    public VoiceVideoLayer DefaultLayer { get; init; } = VoiceVideoLayer.High;

    /// <summary>
    /// The layer chosen for a subscriber who has not reported a tile size in a room that is above
    /// the threshold.
    /// </summary>
    public VoiceVideoLayer GridLayer { get; init; } = VoiceVideoLayer.Medium;

    /// <summary>How long an idle room survives before <see cref="VoiceReconciler.ReapAsync"/> is
    /// willing to drop it. A room whose roster emptied a second ago may be one somebody is mid-join
    /// on, and racing that would hand them a room-gone for a room they are about to be in.</summary>
    public TimeSpan IdleRoomGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the per-room attention blob survives without a write.</summary>
    public TimeSpan AttentionTtl { get; init; } = TimeSpan.FromHours(4);

    public static readonly VoiceSubscriptionOptions Default = new();

    /// <summary>The environment-configured instance.</summary>
    public static VoiceSubscriptionOptions FromEnvironment() => new()
    {
        Enabled = Flag("VOICE_SUBSCRIPTION_PLANNING", Default.Enabled),
        Enforce = Flag("VOICE_ENFORCE_SUBSCRIPTION_PLAN", Default.Enforce),
        ActiveSpeakerThreshold = Number("VOICE_ACTIVE_SPEAKER_THRESHOLD", Default.ActiveSpeakerThreshold),
        ActiveSpeakerCount = Number("VOICE_ACTIVE_SPEAKER_COUNT", Default.ActiveSpeakerCount),
        MaxActiveSpeakers = Number("VOICE_MAX_ACTIVE_SPEAKERS", Default.MaxActiveSpeakers),
        MaxPinnedPerSubscriber = Number("VOICE_MAX_PINNED", Default.MaxPinnedPerSubscriber),
        MaxVideoPublishers = Number("VOICE_MAX_VIDEO_PUBLISHERS", Default.MaxVideoPublishers),
        ScreenShareAudioOptIn = Flag("VOICE_SCREEN_AUDIO_OPT_IN", Default.ScreenShareAudioOptIn),
        SpeakerHoldTime = Seconds("VOICE_SPEAKER_HOLD_SECONDS", Default.SpeakerHoldTime),
        MinimumDwell = Seconds("VOICE_SPEAKER_DWELL_SECONDS", Default.MinimumDwell),
        MinimumSpeechToHold = Milliseconds("VOICE_SPEAKER_MIN_SPEECH_MS", Default.MinimumSpeechToHold),
    };

    private static string? Raw(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int Number(string name, int fallback) =>
        Raw(name) is { } raw
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : fallback;

    private static bool Flag(string name, bool fallback) =>
        Raw(name) is { } raw && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    private static TimeSpan Milliseconds(string name, TimeSpan fallback) =>
        Raw(name) is { } raw
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? TimeSpan.FromMilliseconds(parsed)
            : fallback;

    private static TimeSpan Seconds(string name, TimeSpan fallback) =>
        Raw(name) is { } raw
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : fallback;
}
