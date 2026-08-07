using Echo.Voice.Tracks;

namespace Echo.Voice.Rooms;

/// <summary>
/// Every room lifecycle transition that both guild channels and direct calls share, implemented
/// once.
/// </summary>
public sealed class VoiceRoomService(VoiceRoomStore rooms, VoiceAnnouncer announcer)
{
    /// <summary>Puts a participant in the room before any media work begins.</summary>
    public async Task<VoiceRoom> JoinAsync(
        VoiceRoomKey key, string userId, string? deviceId, string? guildId = null,
        CancellationToken ct = default)
    {
        var room = await rooms.MutateAsync(key, r =>
        {
            var existing = r.Find(userId);
            if (existing is null)
            {
                r.Participants.Add(new VoiceParticipant
                {
                    UserId = userId,
                    DeviceId = deviceId,
                    JoinedAt = DateTime.UtcNow,
                });
            }
            else
            {
                // Same user rejoining, or a takeover that already ran - make the roster reflect
                // the device actually connecting now.
                existing.DeviceId = deviceId;
            }
        }, guildId, ct);

        // The joiner gets the authoritative state immediately, so they never depend on having been
        // connected for earlier events.
        await announcer.SendSnapshotAsync(room, userId, ct);
        return room;
    }

    /// <summary>Removes a participant and tells the room.</summary>
    public async Task<VoiceRoom?> LeaveAsync(
        VoiceRoomKey key, string userId, CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
            r.Participants.RemoveAll(p => p.UserId == userId), ct);
        if (room is null) return null;

        await announcer.ToAllAsync(room, VoiceEvents.Resync,
            new { reason = "participantLeft", userId }, ct);
        return room;
    }

    /// <summary>
    /// Records that a participant published their microphone, and announces them as publishable.
    /// </summary>
    public async Task<VoiceRoom?> RecordPublishAsync(
        VoiceRoomKey key, string userId, string cfSessionId, CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;
            me.CfSessionId = cfSessionId;
            me.AudioTrackName = TrackNaming.Audio;
        }, ct);
        if (room?.Find(userId) is not { } me) return room;

        await announcer.ToOthersAsync(room, userId, VoiceEvents.ParticipantJoined, new
        {
            userId,
            cfSessionId = me.CfSessionId,
            audioTrackName = me.AudioTrackName,
        }, ct);

        // And the publisher gets the current room back, which replaces the two separate
        // hand-rolled backfills that used to live in the call and channel controllers.
        await announcer.SendSnapshotAsync(room, userId, ct);
        return room;
    }

    /// <summary>Records non-microphone tracks (camera, screen share) and announces each.</summary>
    public async Task<VoiceRoom?> RecordTracksAsync(
        VoiceRoomKey key, string userId, string cfSessionId, IReadOnlyList<string> trackNames,
        CancellationToken ct = default)
    {
        var described = trackNames.Select(TrackNaming.Describe).ToList();

        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;

            foreach (var track in described)
            {
                if (track.ShareId is not { } shareId) continue;

                var share = me.ActiveScreenShares.FirstOrDefault(s => s.ShareId == shareId);
                if (share is null)
                {
                    share = new ActiveScreenShare { ShareId = shareId };
                    me.ActiveScreenShares.Add(share);
                }
                if (!share.TrackNames.Contains(track.TrackName))
                    share.TrackNames.Add(track.TrackName);
            }
        }, ct);
        if (room is null) return null;

        foreach (var track in described)
        {
            await announcer.ToOthersAsync(room, userId, VoiceEvents.TrackPublished, new
            {
                userId,
                cfSessionId,
                trackName = track.TrackName,
                kind = track.Kind,
                shareId = track.ShareId,
            }, ct);
        }

        return room;
    }

    /// <summary>Forgets closed tracks and tells the room, so peers drop them rather than waiting
    /// on media that has stopped.</summary>
    public async Task<VoiceRoom?> RecordTracksClosedAsync(
        VoiceRoomKey key, string userId, IReadOnlyList<string> trackNames,
        CancellationToken ct = default)
    {
        var described = trackNames.Select(TrackNaming.Describe).ToList();

        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;

            foreach (var track in described)
            {
                if (TrackNaming.IsMicrophone(track.TrackName))
                {
                    // The publisher stopped their microphone: they are no longer pullable, and
                    // saying so is what stops peers holding a dead handle.
                    me.CfSessionId = null;
                    me.AudioTrackName = null;
                    continue;
                }

                foreach (var share in me.ActiveScreenShares)
                    share.TrackNames.Remove(track.TrackName);
            }
            me.ActiveScreenShares.RemoveAll(s => s.TrackNames.Count == 0);
        }, ct);
        if (room is null) return null;

        foreach (var track in described)
        {
            await announcer.ToOthersAsync(room, userId, VoiceEvents.TrackClosed, new
            {
                userId,
                trackName = track.TrackName,
                shareId = track.ShareId,
            }, ct);
        }

        return room;
    }

    /// <summary>Applies a self- or server-imposed state change and announces it.</summary>
    public async Task<VoiceRoom?> SetMuteAsync(
        VoiceRoomKey key, string targetUserId, bool isMuted, bool serverForced,
        CancellationToken ct = default) =>
        await ApplyFlagAsync(key, targetUserId, VoiceEvents.MuteChanged,
            p =>
            {
                if (serverForced) p.IsServerMuted = isMuted;
                else p.IsSelfMuted = isMuted;
            },
            new { userId = targetUserId, isMuted, serverForced }, serverForced, ct);

    public async Task<VoiceRoom?> SetDeafenAsync(
        VoiceRoomKey key, string targetUserId, bool isDeafened, bool serverForced,
        CancellationToken ct = default) =>
        await ApplyFlagAsync(key, targetUserId, VoiceEvents.DeafenChanged,
            p =>
            {
                if (serverForced) p.IsServerDeafened = isDeafened;
                else p.IsSelfDeafened = isDeafened;
            },
            new { userId = targetUserId, isDeafened, serverForced }, serverForced, ct);

    /// <summary>Camera state is relayed, not stored - it has never been part of the roster.</summary>
    public async Task<VoiceRoom?> SetCameraAsync(
        VoiceRoomKey key, string userId, bool isCameraOn, CancellationToken ct = default)
    {
        var room = await rooms.LoadAsync(key, ct);
        if (room?.Find(userId) is null) return null;

        await announcer.ToOthersAsync(room, userId, VoiceEvents.CameraChanged,
            new { userId, isCameraOn }, ct);
        return room;
    }

    /// <summary>Speaking indicators are pure relay: high frequency, no durable meaning, and
    /// worthless a second later. Deliberately not stored and deliberately not versioned state.</summary>
    public async Task<VoiceRoom?> SetSpeakingAsync(
        VoiceRoomKey key, string userId, bool isSpeaking, CancellationToken ct = default)
    {
        var room = await rooms.LoadAsync(key, ct);
        if (room?.Find(userId) is null) return null;

        await announcer.ToOthersAsync(room, userId, VoiceEvents.SpeakingChanged,
            new { userId, isSpeaking }, ct);
        return room;
    }

    public async Task<VoiceRoom?> SetStreamingAsync(
        VoiceRoomKey key, string userId, bool isStreaming, string shareId,
        CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is not null) me.IsStreaming = isStreaming;
        }, ct);
        if (room?.Find(userId) is null) return null;

        await announcer.ToOthersAsync(room, userId,
            isStreaming ? VoiceEvents.ScreenShareStarted : VoiceEvents.ScreenShareStopped,
            new { userId, shareId }, ct);
        return room;
    }

    /// <summary>Announces the current audience of a screen share to the room.</summary>
    public async Task<VoiceRoom?> AnnounceShareViewersAsync(
        VoiceRoomKey key, string shareId, IReadOnlyList<string> viewerIds, CancellationToken ct = default)
    {
        var room = await rooms.LoadAsync(key, ct);
        if (room is null) return null;

        await announcer.ToAllAsync(room, VoiceEvents.ShareViewersChanged,
            new { shareId, viewerCount = viewerIds.Count, viewerIds }, ct);
        return room;
    }

    /// <summary>The shared body of the flag setters.</summary>
    private async Task<VoiceRoom?> ApplyFlagAsync(
        VoiceRoomKey key, string targetUserId, string eventName,
        Action<VoiceParticipant> apply, object payload, bool toEveryone, CancellationToken ct)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var target = r.Find(targetUserId);
            if (target is not null) apply(target);
        }, ct);

        // The mutation silently no-ops for someone who is not in the room; the broadcast used not
        // to, which is the hole described above.
        if (room?.Find(targetUserId) is null) return null;

        // A moderator action is told to the target as well - they need to know they were muted.
        if (toEveryone) await announcer.ToAllAsync(room, eventName, payload, ct);
        else await announcer.ToOthersAsync(room, targetUserId, eventName, payload, ct);

        return room;
    }
}
