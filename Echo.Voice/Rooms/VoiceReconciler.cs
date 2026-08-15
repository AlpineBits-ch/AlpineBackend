using Echo.Voice.Usage;
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

/// <summary>What a reap pass decided about one room.</summary>
public enum VoiceReapOutcome
{
    /// <summary>There was no room at that key. Already reaped, or never created.</summary>
    NotFound,

    /// <summary>Somebody is still in it and still alive. The overwhelming majority.</summary>
    Live,

    /// <summary>The room was dropped: its roster was empty, or every participant on it had stopped
    /// heartbeating long enough ago to be certain.</summary>
    Closed,
}

/// <summary>
/// Turns the heartbeat from a liveness ping into a state assertion, which is what makes backfill
/// converge rather than merely usually work.
/// </summary>
/// <param name="usage">
/// Optional, and defaulted so that a host which does not register it - or a test that does not care
/// about it - constructs a reconciler unchanged.
/// </param>
/// <param name="subscriptions">Also optional, also defaulted.</param>
public sealed class VoiceReconciler(
    VoiceRoomStore rooms,
    VoiceAnnouncer announcer,
    IDistributedCache cache,
    ILogger<VoiceReconciler> logger,
    VoiceUsageMeter? usage = null,
    VoiceSubscriptions? subscriptions = null)
{
    /// <summary>How long a participant survives without a heartbeat before the sweep evicts them.
    /// Matches <see cref="Echo.Realtime.Caching.StreamViewerStore.ViewerTtl"/> so one client timer
    /// drives both.</summary>
    public static readonly TimeSpan LivenessTtl = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a participant keeps their place after the socket carrying their voice connection
    /// closes, before the sweep is allowed to take them.
    /// </summary>
    public static readonly TimeSpan DisconnectGraceTtl = TimeSpan.FromSeconds(45);

    public static string LivenessKey(string userId) => $"voice:heartbeat:{userId}";

    /// <summary>
    /// Writes the one fact <c>VoiceHeartbeatCleanupService</c> reads: this user is still here, in
    /// this room, as of now - good for a full <see cref="LivenessTtl"/>.
    /// </summary>
    public static Task ClaimLivenessAsync(
        IDistributedCache cache, string userId, VoiceRoomKey key, CancellationToken ct = default) =>
        cache.SetStringAsync(
            LivenessKey(userId), key.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LivenessTtl }, ct);

    /// <summary>Processes one heartbeat.</summary>
    /// <param name="userId">Server-authoritative, taken from the hub context.</param>
    /// <param name="knownInstanceId">
    /// The room incarnation the client believes it is tracking.
    /// </param>
    /// <param name="claimedMediaSessionId">
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
        string? claimedMediaSessionId,
        string? claimedAudioTrack,
        CancellationToken ct = default)
    {
        // Liveness first and unconditionally: even a heartbeat about a room we disagree on proves
        // the client is alive, and dropping it would let the sweep evict a healthy participant.
        await ClaimLivenessAsync(cache, userId, key, ct);

        var room = await rooms.LoadAsync(key, ct);
        if (room is null)
        {
            logger.LogWarning(
                "Heartbeat from {UserId} for room {Room}, which no longer exists - asking them to rejoin",
                userId, key);
            await announcer.SendRoomGoneAsync(key, userId, ct);
            return VoiceReconcileOutcome.RoomGone;
        }

        // Refreshed before the sample, not after, because the plan is what the sample is measured
        // against: a meter handed last minute's ranking reports last minute's bill.
        var (plan, planChanged) = subscriptions is null
            ? (VoiceSubscriptionPlan.Unplanned, false)
            : await subscriptions.RefreshAsync(room, ct);

        if (planChanged && plan.IsSelective)
            await announcer.SendSubscriptionsAsync(room, plan, ct);

        // Sampled here rather than after the reconciliation below, because what is being measured
        // is the room's fan-out and not this caller's opinion of it - a heartbeat from somebody the
        // roster has already dropped still proves the room is live.
        if (usage is not null)
            await usage.SampleAsync(
                room, subscriptions?.Options.Enforce == true ? plan : null, ct);

        var me = room.Find(userId);
        if (me is null)
        {
            // They believe they are in a room whose roster disagrees.
            logger.LogWarning(
                "Heartbeat from {UserId} for room {Room}, which they are not in - asking them to rejoin",
                userId, key);
            await announcer.SendRoomGoneAsync(key, userId, ct);

            // A room whose roster emptied - the sweep evicted the last stale participant, or the
            // last leave raced this read - is one nobody can be in.
            if (room.Participants.Count == 0) await DropAsync(key, ct);

            return VoiceReconcileOutcome.RoomGone;
        }

        var drifted = me.MediaSessionId != claimedMediaSessionId || me.AudioTrackName != claimedAudioTrack;
        if (drifted)
        {
            // Captured inside the mutation, not from the read above.
            VoicePublishState? wasPublishing = null;

            var repaired = await rooms.MutateExistingAsync(key, r =>
            {
                var p = r.Find(userId);
                if (p is null) return;
                wasPublishing = p.PublishState;
                p.MediaSessionId = claimedMediaSessionId;
                p.AudioTrackName = claimedAudioTrack;
            }, ct);

            if (repaired is not null)
            {
                logger.LogInformation(
                    "Repaired {UserId} in room {Room}: session {OldSession}->{NewSession}, "
                    + "track {OldTrack}->{NewTrack}, now v{Version}",
                    userId, key, me.MediaSessionId ?? "null", claimedMediaSessionId ?? "null",
                    me.AudioTrackName ?? "null", claimedAudioTrack ?? "null", repaired.Version);

                var now = repaired.Find(userId)!;

                // Peers only need telling when what they can pull changed.
                if (now.PublishState != wasPublishing)
                    await announcer.ToOthersAsync(repaired, userId, VoiceAnnouncer.ResyncEvent,
                        new { reason = "peerPublishChanged", userId }, ct);

                await announcer.SendSnapshotAsync(repaired, userId, plan, ct);
                return VoiceReconcileOutcome.Repaired;
            }
        }

        // Any disagreement, in either direction, and any change of incarnation.
        if (knownInstanceId != room.InstanceId || knownVersion != room.Version)
        {
            await announcer.SendSnapshotAsync(room, userId, plan, ct);
            return VoiceReconcileOutcome.SnapshotSent;
        }

        return VoiceReconcileOutcome.InSync;
    }

    /// <summary>Closes a room that nobody is in any more.</summary>
    public async Task<VoiceReapOutcome> ReapAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        var options = subscriptions?.Options ?? VoiceSubscriptionOptions.Default;

        var room = await rooms.LoadAsync(key, ct);
        if (room is null) return VoiceReapOutcome.NotFound;

        if (room.Participants.Count == 0) return await DropAsync(key, ct);

        var cutoff = DateTime.UtcNow - options.IdleRoomGrace;
        if (room.Participants.Any(p => p.JoinedAt > cutoff)) return VoiceReapOutcome.Live;

        foreach (var participant in room.Participants)
        {
            if (await cache.GetStringAsync(LivenessKey(participant.UserId), ct) is not null)
                return VoiceReapOutcome.Live;
        }

        // Told before the roster is cleared, because the whole premise is that these clients have
        // stopped heartbeating - and one of them may be a live client whose liveness key expired
        // during an outage rather than a dead one.
        await announcer.ToAllAsync(room, VoiceEvents.Resync, new { reason = "roomReaped" }, ct);

        logger.LogInformation(
            "Reaping voice room {Room}: {Count} participants, none heartbeating",
            key, room.Participants.Count);

        await rooms.MutateExistingAsync(key, r => r.Participants.Clear(), ct);
        return await DropAsync(key, ct);
    }

    private async Task<VoiceReapOutcome> DropAsync(VoiceRoomKey key, CancellationToken ct)
    {
        if (!await rooms.RemoveIfEmptyAsync(key, ct)) return VoiceReapOutcome.Live;

        if (subscriptions is not null) await subscriptions.DropAsync(key, ct);
        return VoiceReapOutcome.Closed;
    }
}
