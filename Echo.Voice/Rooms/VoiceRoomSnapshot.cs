using Echo.Entitlements.Wire;

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

/// <param name="MediaSessionId">The session the share is published on, which is not necessarily the
/// publisher's microphone session - see <see cref="ActiveScreenShare.MediaSessionId"/>. Null on
/// shares that predate it being recorded, where the handle is only in the <c>TrackPublished</c>
/// event.</param>
public sealed record VoiceShareSnapshot(
    string ShareId, IReadOnlyList<string> TrackNames, string? MediaSessionId);

/// <summary>
/// The complete, authoritative state of a room, and the only thing a client needs in order to be
/// correct - whatever it missed, whenever it asks.
/// </summary>
/// <param name="Subscriptions">
/// What the recipient should be pulling, when the snapshot was built for a particular recipient and
/// subscription planning is active.
/// </param>
/// <param name="Limits">
/// The room's entitlement ceilings, null on a room whose limits have never been resolved - which
/// means "no limit information", not "no limits".
/// </param>
public sealed record VoiceRoomSnapshot(
    string RoomId,
    string Kind,
    string? GuildId,
    string InstanceId,
    long Version,
    IReadOnlyList<VoiceParticipantSnapshot> Participants,
    VoiceSubscriptionSnapshot? Subscriptions = null,
    VoiceRoomLimitsDto? Limits = null)
{
    /// <summary>The only way to produce a snapshot, so it cannot be complete for one room kind and
    /// lossy for the other.</summary>
    public static VoiceRoomSnapshot From(VoiceRoom room) => From(room, null, null);

    /// <summary>The recipient-specific snapshot.</summary>
    public static VoiceRoomSnapshot From(
        VoiceRoom room, VoiceSubscriptionPlan? plan, string? forUserId) => new(
        room.RoomId,
        room.Kind,
        room.GuildId,
        room.InstanceId,
        room.Version,
        room.Participants.Select(Project).ToList(),
        plan is { IsSelective: true } && forUserId is not null
            ? new VoiceSubscriptionSnapshot(
                plan.Mode, plan.Revision, plan.ActiveSpeakers, plan.For(forUserId).Tracks)
            : null,
        // Projected here rather than passed in, so that every snapshot carries them - the join
        // reply, the recovery push and the reconciler's repair alike.
        room.Limits?.ToDto(room.Participants.Count(VoiceSubscriptionPlanner.HasVideo)));

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
        p.ActiveScreenShares
            .Select(s => new VoiceShareSnapshot(s.ShareId, s.TrackNames, s.MediaSessionId))
            .ToList(),
        p.JoinedAt);
}

/// <summary>
/// The subscription half of a snapshot: what this recipient should be pulling, and the ranking it
/// came from.
/// </summary>
public sealed record VoiceSubscriptionSnapshot(
    string Mode,
    long Revision,
    IReadOnlyList<string> ActiveSpeakers,
    IReadOnlyList<VoiceSubscription> Tracks);
