namespace Echo.Voice.Rooms;

/// <summary>One participant as a client sees them, including what to pull to hear them.</summary>
public sealed record VoiceParticipantSnapshot(
    string UserId,
    string? MediaSessionId,
    string? AudioTrackName,
    string PublishState,
    bool IsSelfMuted,
    bool IsSelfDeafened,
    bool IsServerMuted,
    bool IsServerDeafened,
    bool IsStreaming,
    IReadOnlyList<VoiceShareSnapshot> Shares,
    DateTime JoinedAt);

public sealed record VoiceShareSnapshot(string ShareId, IReadOnlyList<string> TrackNames);

/// <summary>
/// The complete, authoritative state of a room, and the only thing a client needs in order to be
/// correct - whatever it missed, whenever it asks.
/// </summary>
public sealed record VoiceRoomSnapshot(
    string RoomId,
    string Kind,
    string? GuildId,
    string InstanceId,
    long Version,
    IReadOnlyList<VoiceParticipantSnapshot> Participants)
{
    /// <summary>The only way to produce a snapshot, so it cannot be complete for one room kind and
    /// lossy for the other.</summary>
    public static VoiceRoomSnapshot From(VoiceRoom room) => new(
        room.RoomId,
        room.Kind,
        room.GuildId,
        room.InstanceId,
        room.Version,
        room.Participants.Select(Project).ToList());

    /// <summary>
    /// An empty room at version 0 - what a caller gets for a room that does not exist.
    /// </summary>
    public static VoiceRoomSnapshot Empty(VoiceRoomKey key, string? guildId = null) =>
        new(key.Id, key.Kind, guildId, string.Empty, 0, []);

    private static VoiceParticipantSnapshot Project(VoiceParticipant p) => new(
        p.UserId,
        // Withheld unless actually publishing.
        p.PublishState == VoicePublishState.Publishing ? p.MediaSessionId : null,
        p.PublishState == VoicePublishState.Publishing ? p.AudioTrackName : null,
        p.PublishState.ToString(),
        p.IsSelfMuted,
        p.IsSelfDeafened,
        p.IsServerMuted,
        p.IsServerDeafened,
        p.IsStreaming,
        p.ActiveScreenShares.Select(s => new VoiceShareSnapshot(s.ShareId, s.TrackNames)).ToList(),
        p.JoinedAt);
}
