namespace Echo.Voice.Rooms;

/// <summary>Every voice event name, in one place, unprefixed.</summary>
public static class VoiceEvents
{
    // ── Shared: identical meaning in both room kinds ──────────────────────────

    /// <summary>A participant is now publishable - their session and microphone track both exist.
    /// Never sent for someone who has merely opened a session.</summary>
    public const string ParticipantJoined = "ParticipantJoined";

    public const string TrackPublished = "TrackPublished";
    public const string TrackClosed = "TrackClosed";
    public const string MuteChanged = "MuteChanged";
    public const string DeafenChanged = "DeafenChanged";
    public const string CameraChanged = "CameraChanged";
    public const string SpeakingChanged = "SpeakingChanged";
    public const string ScreenShareStarted = "ScreenShareStarted";
    public const string ScreenShareStopped = "ScreenShareStopped";
    public const string ShareViewersChanged = "ShareViewersChanged";

    /// <summary>"This is what you should be pulling now."</summary>
    public const string SubscriptionsChanged = "SubscriptionsChanged";

    // ── Recovery ──────────────────────────────────────────────────────────────

    /// <summary>Full authoritative state. See <see cref="VoiceRoomSnapshot"/>.</summary>
    public const string Snapshot = VoiceAnnouncer.SnapshotEvent;

    /// <summary>"Refetch, you are behind." Carries a reason but never a delta.</summary>
    public const string Resync = VoiceAnnouncer.ResyncEvent;

    /// <summary>
    /// Names that exist in one room kind only, because the underlying concept does.
    /// </summary>
    public static class KindSpecific
    {
        // Guild rooms only.
        public const string UserJoinedVoice = "UserJoinedVoice";
        public const string UserLeftVoice = "UserLeftVoice";
        public const string MovedToChannel = "MovedToChannel";
        public const string KickedByOtherDevice = "KickedByOtherDevice";

        // Calls only.
        public const string IncomingCall = "IncomingCall";
        public const string CallEnded = "CallEnded";
    }
}
