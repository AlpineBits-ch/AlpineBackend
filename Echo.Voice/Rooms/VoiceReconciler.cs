using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Echo.Voice.Rooms;

/// <summary>What a heartbeat turned out to mean.</summary>
public enum VoiceReconcileOutcome
{
    /// <summary>Client and server agreed. The common case.</summary>
    InSync,

    /// <summary>The client was behind and has been sent a snapshot.</summary>
    SnapshotSent,

    /// <summary>The server's record of the caller's media was wrong and has been corrected.</summary>
    Repaired,

    /// <summary>The room no longer exists, or the caller is not in it. They were told to rejoin.</summary>
    RoomGone,
}

/// <summary>
/// Turns the heartbeat from a liveness ping into a state assertion, which is what makes backfill
/// converge rather than merely usually work.
/// </summary>
public sealed class VoiceReconciler(
    VoiceRoomStore rooms,
    VoiceAnnouncer announcer,
    IDistributedCache cache,
    ILogger<VoiceReconciler> logger)
{
    /// <summary>How long a participant survives without a heartbeat before the sweep evicts them.
    /// Matches <see cref="Echo.Realtime.Caching.StreamViewerStore.ViewerTtl"/> so one client timer
    /// drives both.</summary>
    public static readonly TimeSpan LivenessTtl = TimeSpan.FromSeconds(90);

    public static string LivenessKey(string userId) => $"voice:heartbeat:{userId}";

    /// <summary>Processes one heartbeat.</summary>
    /// <param name="userId">Server-authoritative, taken from the hub context.</param>
    /// <param name="knownInstanceId">
    /// The room incarnation the client believes it is tracking.
    /// </param>
    /// <param name="claimedCfSessionId">
    /// The session the client believes it is publishing on, or null if it is not publishing.
    /// </param>
    /// <param name="claimedAudioTrack">
    /// The microphone track the client believes it has published.
    /// </param>
    public async Task<VoiceReconcileOutcome> HeartbeatAsync(
        string userId,
        VoiceRoomKey key,
        string? knownInstanceId,
        long knownVersion,
        string? claimedCfSessionId,
        string? claimedAudioTrack,
        CancellationToken ct = default)
    {
        // Liveness first and unconditionally: even a heartbeat about a room we disagree on proves
        // the client is alive, and dropping it would let the sweep evict a healthy participant.
        await cache.SetStringAsync(
            LivenessKey(userId), key.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LivenessTtl }, ct);

        var room = await rooms.LoadAsync(key, ct);
        if (room is null)
        {
            logger.LogWarning(
                "Heartbeat from {UserId} for room {Room}, which no longer exists - asking them to rejoin",
                userId, key);
            await announcer.SendRoomGoneAsync(key, userId, ct);
            return VoiceReconcileOutcome.RoomGone;
        }

        var me = room.Find(userId);
        if (me is null)
        {
            // They believe they are in a room whose roster disagrees.
            logger.LogWarning(
                "Heartbeat from {UserId} for room {Room}, which they are not in - asking them to rejoin",
                userId, key);
            await announcer.SendRoomGoneAsync(key, userId, ct);
            return VoiceReconcileOutcome.RoomGone;
        }

        var drifted = me.CfSessionId != claimedCfSessionId || me.AudioTrackName != claimedAudioTrack;
        if (drifted)
        {
            // Captured inside the mutation, not from the read above.
            VoicePublishState? wasPublishing = null;

            var repaired = await rooms.MutateExistingAsync(key, r =>
            {
                var p = r.Find(userId);
                if (p is null) return;
                wasPublishing = p.PublishState;
                p.CfSessionId = claimedCfSessionId;
                p.AudioTrackName = claimedAudioTrack;
            }, ct);

            if (repaired is not null)
            {
                logger.LogInformation(
                    "Repaired {UserId} in room {Room}: session {OldSession}->{NewSession}, "
                    + "track {OldTrack}->{NewTrack}, now v{Version}",
                    userId, key, me.CfSessionId ?? "null", claimedCfSessionId ?? "null",
                    me.AudioTrackName ?? "null", claimedAudioTrack ?? "null", repaired.Version);

                var now = repaired.Find(userId)!;

                // Peers only need telling when what they can pull changed.
                if (now.PublishState != wasPublishing)
                    await announcer.ToOthersAsync(repaired, userId, VoiceAnnouncer.ResyncEvent,
                        new { reason = "peerPublishChanged", userId }, ct);

                await announcer.SendSnapshotAsync(repaired, userId, ct);
                return VoiceReconcileOutcome.Repaired;
            }
        }

        // Any disagreement, in either direction, and any change of incarnation.
        if (knownInstanceId != room.InstanceId || knownVersion != room.Version)
        {
            await announcer.SendSnapshotAsync(room, userId, ct);
            return VoiceReconcileOutcome.SnapshotSent;
        }

        return VoiceReconcileOutcome.InSync;
    }
}
