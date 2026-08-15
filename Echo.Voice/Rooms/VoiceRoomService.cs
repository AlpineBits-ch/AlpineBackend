using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;
using Microsoft.Extensions.Logging;

namespace Echo.Voice.Rooms;

/// <summary>
/// Every room lifecycle transition that both guild channels and direct calls share, implemented
/// once.
/// </summary>
/// <param name="subscriptions">
/// Optional, and defaulted so a host that does not register it - or a test that does not care -
/// gets the all-to-all behaviour this service had before subscription planning existed.
/// </param>
/// <param name="entitlements">Also optional.</param>
/// <param name="operatorCeilings">
/// What this box will carry, as opposed to what a guild has paid for.
/// </param>
/// <param name="logger">
/// Only ever used to record that a limit could not be resolved, which is a condition this class
/// swallows rather than propagates - see <see cref="ResolvedVoiceLimits.Unresolved"/>.
/// </param>
public sealed class VoiceRoomService(
    VoiceRoomStore rooms,
    VoiceAnnouncer announcer,
    VoiceSubscriptions? subscriptions = null,
    EntitlementResolver? entitlements = null,
    OperatorCeilings? operatorCeilings = null,
    ILogger<VoiceRoomService>? logger = null)
{
    private readonly OperatorCeilings _ceilings = operatorCeilings ?? OperatorCeilings.None;

    private VoiceSubscriptionOptions Options => subscriptions?.Options ?? VoiceSubscriptionOptions.Default;

    /// <summary>Puts a participant in the room before any media work begins.</summary>
    public async Task<VoiceRoom> JoinAsync(
        VoiceRoomKey key, string userId, string? deviceId, string? guildId = null,
        CancellationToken ct = default) =>
        (await AdmitAsync(key, userId, deviceId, guildId, ct)).Room;

    /// <summary>
    /// The join, plus what the room's limits are and whether this joiner is past them.
    /// </summary>
    public async Task<VoiceAdmission> AdmitAsync(
        VoiceRoomKey key, string userId, string? deviceId, string? guildId = null,
        CancellationToken ct = default)
    {
        // Resolved before the mutation because the mutation has to be idempotent - it is re-run from
        // a fresh read on every contention retry - and a resolver call inside it would be made once
        // per attempt for a value that cannot change between them.
        var limits = await ResolveLimitsAsync(guildId, ct);

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

            // Skipped when the resolver could not answer, so a room keeps the last limits somebody
            // successfully resolved rather than being told it has none.
            if (limits.Resolved) r.Limits = limits.Limits;
        }, guildId, ct);

        // A joiner changes who is eligible for a ranked slot, so the plan is refreshed before the
        // snapshot is built rather than after it - a joiner handed a plan computed without them in
        // it would be told to pull nothing and would hear nothing until the next recomputation.
        var (plan, changed) = await ReselectAsync(room, ct);

        // The joiner gets the authoritative state immediately, so they never depend on having been
        // connected for earlier events.
        await announcer.SendSnapshotAsync(room, userId, plan, ct);
        await AnnouncePlanAsync(room, plan, changed, ct);

        return Admit(room, userId, limits);
    }

    /// <summary>
    /// Where one participant stands against the room's capacity, and the degradation if they are
    /// past it.
    /// </summary>
    private VoiceAdmission Admit(VoiceRoom room, string userId, ResolvedVoiceLimits limits)
    {
        var position = PositionOf(room, userId);
        var cap = limits.Limits.MaxParticipants;

        if (position == 0 || position <= cap)
        {
            return new VoiceAdmission(room, limits.Limits, position, null);
        }

        return new VoiceAdmission(room, limits.Limits, position, new VoiceDegradation(
            EntitlementKeys.VoiceMaxParticipants,
            // The room's size as it now stands, not this one person, because a client comparing a
            // headcount of one against a cap of ten would render a meter that never fills.
            EntitlementValue.OfNumber(position),
            EntitlementValue.OfNumber(cap),
            limits.Participants,
            SubjectOf(room, userId, limits.Participants.BoundBy)));
    }

    /// <summary>Removes a participant and tells the room.</summary>
    public async Task<VoiceRoom?> LeaveAsync(
        VoiceRoomKey key, string userId, CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
            r.Participants.RemoveAll(p => p.UserId == userId), ct);
        if (room is null) return null;

        if (room.Participants.Count == 0)
        {
            if (await rooms.RemoveIfEmptyAsync(key, ct) && subscriptions is not null)
                await subscriptions.DropAsync(key, ct);

            // Returned rather than null, because callers read the guild id off it to finish their
            // own cleanup, and there is nobody left to announce anything to.
            return room;
        }

        await announcer.ToAllAsync(room, VoiceEvents.Resync,
            new { reason = "participantLeft", userId }, ct);

        if (subscriptions is not null)
        {
            var (plan, changed) = await subscriptions.ForgetAsync(room, userId, ct);
            await AnnouncePlanAsync(room, plan, changed, ct);
        }

        return room;
    }

    /// <summary>
    /// Records that a participant published their microphone, and announces them as publishable.
    /// </summary>
    public async Task<VoiceRoom?> RecordPublishAsync(
        VoiceRoomKey key, string userId, string mediaSessionId, CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;
            me.MediaSessionId = mediaSessionId;
            me.AudioTrackName = TrackNaming.Audio;
        }, ct);
        if (room?.Find(userId) is not { } me) return room;

        await announcer.ToOthersAsync(room, userId, VoiceEvents.ParticipantJoined, new
        {
            userId,
            mediaSessionId = me.MediaSessionId,
            audioTrackName = me.AudioTrackName,
        }, ct);

        // Publishing is what makes somebody eligible for a ranked slot, so this is the other point
        // besides speech at which the set genuinely has to move.
        var (plan, changed) = await ReselectAsync(room, ct);

        // And the publisher gets the current room back, which replaces the two separate
        // hand-rolled backfills that used to live in the call and channel controllers.
        await announcer.SendSnapshotAsync(room, userId, plan, ct);
        await AnnouncePlanAsync(room, plan, changed, ct);
        return room;
    }

    /// <summary>Records non-microphone tracks (camera, screen share) and announces each.</summary>
    /// <param name="maxLayer">
    /// The best simulcast layer of this publisher's video that may be distributed, from <see
    /// cref="VoicePublishDecision.MaxLayer"/>.
    /// </param>
    public async Task<VoiceRoom?> RecordTracksAsync(
        VoiceRoomKey key, string userId, string mediaSessionId, IReadOnlyList<string> trackNames,
        VoiceVideoLayer? maxLayer = null, CancellationToken ct = default)
    {
        var described = trackNames.Select(TrackNaming.Describe).ToList();

        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;

            me.MaxVideoLayer = maxLayer;

            foreach (var track in described)
            {
                if (track.ShareId is not { } shareId)
                {
                    // A camera, or anything else unrecognised.
                    if (TrackNaming.IsMicrophone(track.TrackName)) continue;

                    var video = me.ActiveVideoTracks
                        .FirstOrDefault(v => v.TrackName == track.TrackName);
                    if (video is null)
                        me.ActiveVideoTracks.Add(video = new ActiveVideoTrack { TrackName = track.TrackName });
                    video.MediaSessionId = mediaSessionId;
                    continue;
                }

                var share = me.ActiveScreenShares.FirstOrDefault(s => s.ShareId == shareId);
                if (share is null)
                {
                    share = new ActiveScreenShare { ShareId = shareId };
                    me.ActiveScreenShares.Add(share);
                }
                if (!share.TrackNames.Contains(track.TrackName))
                    share.TrackNames.Add(track.TrackName);

                // Recorded so that anything reconstructing state from the roster alone can address
                // the share.
                share.MediaSessionId = mediaSessionId;
            }
        }, ct);
        if (room is null) return null;

        foreach (var track in described)
        {
            await announcer.ToOthersAsync(room, userId, VoiceEvents.TrackPublished, new
            {
                userId,
                mediaSessionId,
                trackName = track.TrackName,
                kind = track.Kind,
                shareId = track.ShareId,
            }, ct);
        }

        var (plan, changed) = await ReselectAsync(room, ct);
        await AnnouncePlanAsync(room, plan, changed, ct, force: true);
        return room;
    }

    /// <summary>Whether one more participant may distribute video in this room.</summary>
    public async Task<bool> CanPublishVideoAsync(
        VoiceRoomKey key, string userId, CancellationToken ct = default) =>
        (await EvaluateVideoPublishAsync(key, userId, VoiceVideoRequest.Best, ct)).VideoAllowed;

    /// <summary>
    /// What this participant may actually publish, and why it is not what they asked for.
    /// </summary>
    /// <param name="request">What the publisher intends to send.</param>
    public async Task<VoicePublishDecision> EvaluateVideoPublishAsync(
        VoiceRoomKey key, string userId, VoiceVideoRequest request, CancellationToken ct = default)
    {
        var room = await rooms.LoadAsync(key, ct);
        var limits = await ResolveLimitsAsync(room?.GuildId, ct);

        // No room means nothing to enforce against - the same answer CanPublishVideoAsync gives, and
        // for the same reason: refusing a publish into a room the server has not created yet would
        // fail the first publisher of every call.
        if (room is null) return VoicePublishDecision.Unconstrained(limits.Limits, request);

        var ceiling = await ResolveVideoCeilingAsync(room, userId, limits, ct);
        var ladder = EntitlementLadders.VideoQuality;
        var granted = ladder.RungAt(ceiling.Rank);
        var requested = ladder.RankOf(VideoRungs.RungFor(ladder, request.Height, request.Framerate));
        var publishers = room.Participants.Count(VoiceSubscriptionPlanner.HasVideo);

        if (ceiling.Rank == ladder.LowestRank)
        {
            return VoicePublishDecision.Refused(limits.Limits, publishers, new VoiceDegradation(
                EntitlementKeys.VoiceVideoCeiling,
                EntitlementValue.OfRank(requested),
                EntitlementValue.OfRank(ceiling.Rank),
                ceiling.Cause,
                SubjectOf(room, userId, ceiling.Cause.BoundBy)));
        }

        if (!HasPublisherSlot(room, userId, limits.Limits.MaxPublishers))
        {
            return VoicePublishDecision.Refused(limits.Limits, publishers, new VoiceDegradation(
                EntitlementKeys.VoiceMaxPublishers,
                // The count this publish would produce against the count allowed, so the client can
                // write "2 of 2 people are sharing" rather than the mystery that is "you cannot
                // share".
                EntitlementValue.OfNumber(publishers + 1),
                EntitlementValue.OfNumber(limits.Limits.MaxPublishers),
                limits.Publishers,
                SubjectOf(room, userId, limits.Publishers.BoundBy)));
        }

        var (height, framerate) = VideoRungs.Clamp(granted, request.Height, request.Framerate);

        var reduced = requested <= ceiling.Rank
            ? null
            : new VoiceDegradation(
                EntitlementKeys.VoiceVideoCeiling,
                EntitlementValue.OfRank(requested),
                EntitlementValue.OfRank(ceiling.Rank),
                ceiling.Cause,
                SubjectOf(room, userId, ceiling.Cause.BoundBy));

        return new VoicePublishDecision(
            true, granted, height, framerate, limits.Limits, publishers,
            reduced is null ? [] : [reduced], null,
            // What the ceiling costs a publisher who ignores the clamp.
            VoiceVideoLayers.CeilingFor(granted, request.DeclaredHeight));
    }

    /// <summary>
    /// Re-applies the video ceiling to a publisher who changed what they are sending without
    /// republishing.
    /// </summary>
    /// <param name="request">What the publisher now intends to send.</param>
    public async Task<VoiceLayerRevision> ReviseVideoLayerAsync(
        VoiceRoomKey key, string userId, VoiceVideoRequest request, CancellationToken ct = default)
    {
        if (request.DeclaredHeight <= 0) return VoiceLayerRevision.Undeclared;

        var room = await rooms.LoadAsync(key, ct);
        if (room?.Find(userId) is not { } publisher) return VoiceLayerRevision.Undeclared;

        // Somebody with no video on the roster has nothing to cap, and writing a layer for them
        // would put a Redis write on every renegotiation of every audio-only participant.
        if (!VoiceSubscriptionPlanner.HasVideo(publisher))
            return VoiceLayerRevision.Undeclared;

        var limits = await ResolveLimitsAsync(room.GuildId, ct);

        // An unresolved ceiling means the resolver could not answer, and the fail-open direction
        // that is right at publish time is wrong here: it would read as "unlimited" and lift a cap
        // that a successful resolution had already applied.
        if (!limits.Resolved) return VoiceLayerRevision.Undeclared;

        var ceiling = await ResolveVideoCeilingAsync(room, userId, limits, ct);
        var layer = VoiceVideoLayers.CeilingFor(
            EntitlementLadders.VideoQuality.RungAt(ceiling.Rank), request.DeclaredHeight);

        if (layer == publisher.MaxVideoLayer) return new VoiceLayerRevision(false, layer);

        var updated = await rooms.MutateExistingAsync(key, r =>
        {
            if (r.Find(userId) is { } me) me.MaxVideoLayer = layer;
        }, ct);
        if (updated is null) return VoiceLayerRevision.Undeclared;

        // Forced, like the publish path and for the same reason: the ranked set has not moved and
        // yet what every viewer of this publisher should be pulling has.
        var (plan, changed) = await ReselectAsync(updated, ct);
        await AnnouncePlanAsync(updated, plan, changed, ct, force: true);

        return new VoiceLayerRevision(true, layer);
    }

    /// <summary>
    /// Re-resolves the room's limits and, if they moved, writes them - which advances the version.
    /// </summary>
    public async Task<VoiceRoom?> RefreshLimitsAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        var current = await rooms.LoadAsync(key, ct);
        if (current is null) return null;

        var resolved = await ResolveLimitsAsync(current.GuildId, ct);
        if (!resolved.Resolved || current.Limits == resolved.Limits) return current;

        var room = await rooms.MutateExistingAsync(key, r => r.Limits = resolved.Limits, ct);
        if (room is null) return null;

        await announcer.ToAllAsync(room, VoiceEvents.Resync, new { reason = "limitsChanged" }, ct);
        return room;
    }

    /// <summary>
    /// The subscribed tracks a <c>TrackNotFound</c> can be blamed on, for <see
    /// cref="RecordTracksMissingAsync"/>.
    /// </summary>
    public static IReadOnlyList<string> AttributableSubscribes(IEnumerable<VoiceTrackRef> tracks)
    {
        var subscribes = tracks
            .Where(t => t.Direction == VoiceTrackDirection.Subscribe && t.TrackName is not null)
            .ToList();
        return subscribes.Count == 1 ? [subscribes[0].TrackName!] : [];
    }

    /// <summary>Forgets tracks the SFU says do not exist, whoever published them.</summary>
    public async Task<VoiceRoom?> RecordTracksMissingAsync(
        VoiceRoomKey key, IReadOnlyList<string> trackNames, CancellationToken ct = default)
    {
        var described = trackNames.Select(TrackNaming.Describe).ToList();
        // Resolved inside the mutation, because the announcement has to name the publisher and the
        // request that triggered this came from a subscriber, who does not know who that is.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        var room = await rooms.MutateExistingAsync(key, r =>
        {
            foreach (var track in described)
            {
                // Microphone tracks are deliberately left alone.
                if (TrackNaming.IsMicrophone(track.TrackName)) continue;

                foreach (var participant in r.Participants)
                {
                    if (!participant.ActiveScreenShares.Any(s => s.TrackNames.Contains(track.TrackName)))
                        continue;

                    owners[track.TrackName] = participant.UserId;
                    foreach (var share in participant.ActiveScreenShares)
                        share.TrackNames.Remove(track.TrackName);
                    participant.ActiveScreenShares.RemoveAll(s => s.TrackNames.Count == 0);
                    // Consistent with SetStreamingAsync: still streaming only while some other
                    // share of theirs survives, so the flag can never contradict the list.
                    participant.IsStreaming = participant.ActiveScreenShares.Count > 0;
                }
            }
        }, ct);
        if (room is null) return null;

        foreach (var track in described)
        {
            // Only what was actually on the roster.
            if (!owners.TryGetValue(track.TrackName, out var userId)) continue;

            await announcer.ToAllAsync(room, VoiceEvents.TrackClosed, new
            {
                userId,
                trackName = track.TrackName,
                shareId = track.ShareId,
            }, ct);
        }

        var (plan, changed) = await ReselectAsync(room, ct);
        await AnnouncePlanAsync(room, plan, changed, ct, force: true);
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
                    me.MediaSessionId = null;
                    me.AudioTrackName = null;
                    continue;
                }

                me.ActiveVideoTracks.RemoveAll(v => v.TrackName == track.TrackName);
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

        var (plan, changed) = await ReselectAsync(room, ct);
        await AnnouncePlanAsync(room, plan, changed, ct, force: true);
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

    /// <summary>
    /// Speaking indicators are pure relay to peers: high frequency, no durable meaning, and
    /// worthless a second later.
    /// </summary>
    public async Task<VoiceRoom?> SetSpeakingAsync(
        VoiceRoomKey key, string userId, bool isSpeaking, CancellationToken ct = default)
    {
        var room = await rooms.LoadAsync(key, ct);
        if (room?.Find(userId) is null) return null;

        await announcer.ToOthersAsync(room, userId, VoiceEvents.SpeakingChanged,
            new { userId, isSpeaking }, ct);

        if (subscriptions is not null)
        {
            var (plan, changed) = await subscriptions.RecordSpeakingAsync(room, userId, isSpeaking, ct);
            await AnnouncePlanAsync(room, plan, changed, ct);
        }

        return room;
    }

    /// <summary>
    /// Applies what one subscriber has reported about its own rendering - what it has pinned, what
    /// it has collapsed, how large it draws each tile, and whether it wants a share's audio.
    /// </summary>
    public async Task<VoiceSubscriptionPlan> SetSubscriberAsync(
        VoiceRoomKey key, string userId, VoiceSubscriberUpdate update, CancellationToken ct = default)
    {
        if (subscriptions is null) return VoiceSubscriptionPlan.Unplanned;

        var room = await rooms.LoadAsync(key, ct);
        if (room?.Find(userId) is null) return VoiceSubscriptionPlan.Unplanned;

        var (plan, _) = await subscriptions.UpdateSubscriberAsync(room, userId, update, ct);

        // Only the caller is told, whatever else moved.
        await announcer.ToUserAsync(room, userId, VoiceEvents.SubscriptionsChanged, new
        {
            mode = plan.Mode,
            revision = plan.Revision,
            activeSpeakers = plan.ActiveSpeakers,
            tracks = plan.For(userId).Tracks,
        }, ct);

        return plan;
    }

    /// <summary>The current plan for a room, for a caller that wants it without waiting for the
    /// next push. Null room, or planning not registered, reads as all-to-all.</summary>
    public async Task<VoiceSubscriptionPlan> GetSubscriptionsAsync(
        VoiceRoomKey key, CancellationToken ct = default)
    {
        if (subscriptions is null) return VoiceSubscriptionPlan.Unplanned;

        var room = await rooms.LoadAsync(key, ct);
        return room is null
            ? VoiceSubscriptionPlan.Unplanned
            : await subscriptions.PlanAsync(room, ct);
    }

    /// <summary>
    /// The tracks in a subscribe request that this subscriber's plan does not include.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindUnplannedSubscriptionsAsync(
        VoiceRoomKey key, string subscriberUserId, IEnumerable<VoiceTrackRef> tracks,
        CancellationToken ct = default) =>
        (await PrepareSubscribeAsync(key, subscriberUserId, tracks.ToList(), ct)).Unplanned;

    /// <summary>
    /// Everything the plan has to say about one subscribe request: which of its tracks the
    /// subscriber is not supposed to be pulling, and which simulcast layer each of the rest should
    /// be served at.
    /// </summary>
    public async Task<VoiceSubscribeDecision> PrepareSubscribeAsync(
        VoiceRoomKey key, string subscriberUserId, IReadOnlyList<VoiceTrackRef> tracks,
        CancellationToken ct = default)
    {
        var unchanged = new VoiceSubscribeDecision([], tracks);

        var subscribes = tracks
            .Where(t => t.Direction == VoiceTrackDirection.Subscribe && t.TrackName is not null)
            .ToList();
        if (subscribes.Count == 0 || subscriptions is null) return unchanged;

        var room = await rooms.LoadAsync(key, ct);
        if (room is null) return unchanged;

        var plan = await subscriptions.PlanAsync(room, ct);

        var planned = new Dictionary<string, VoiceSubscription>(StringComparer.Ordinal);
        foreach (var subscription in plan.For(subscriberUserId).Tracks)
            planned[subscription.MatchKey()] = subscription;

        // Only a selective plan can refuse anything, and only while the plan is binding.
        var unplanned = subscriptions.Options.Enforce && plan.IsSelective
            ? subscribes
                .Where(t => !planned.ContainsKey(
                    VoiceSubscription.MatchKey(t.MediaSessionId, t.TrackName!)))
                .Select(t => t.TrackName!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

        if (!subscriptions.Options.SendPreferredRid || planned.Count == 0)
            return new VoiceSubscribeDecision(unplanned, tracks);

        var layered = tracks.Select(track =>
        {
            if (track.Direction != VoiceTrackDirection.Subscribe || track.TrackName is null)
                return track;

            var match = VoiceSubscription.MatchKey(track.MediaSessionId, track.TrackName);
            if (!planned.TryGetValue(match, out var subscription) || subscription.Layer is null)
                return track;

            return track with { Layer = Lower(track.Layer, subscription.Layer) };
        }).ToList();

        return new VoiceSubscribeDecision(unplanned, layered);
    }

    /// <summary>The cheaper of what the client asked for and what the plan decided.</summary>
    private static string Lower(string? requested, string planned) =>
        VoiceVideoLayers.Parse(requested) is { } asked
        && VoiceVideoLayers.Parse(planned) is { } decided
        && asked < decided
            ? requested!
            : planned;

    /// <summary>Starts or stops a screen share.</summary>
    public async Task<VoiceRoom?> SetStreamingAsync(
        VoiceRoomKey key, string userId, bool isStreaming, string shareId,
        CancellationToken ct = default)
    {
        var room = await rooms.MutateExistingAsync(key, r =>
        {
            var me = r.Find(userId);
            if (me is null) return;

            if (!isStreaming) me.ActiveScreenShares.RemoveAll(s => s.ShareId == shareId);

            // Still streaming overall only if some other share of theirs is still live.
            me.IsStreaming = isStreaming || me.ActiveScreenShares.Count > 0;
        }, ct);
        if (room?.Find(userId) is null) return null;

        await announcer.ToOthersAsync(room, userId,
            isStreaming ? VoiceEvents.ScreenShareStarted : VoiceEvents.ScreenShareStopped,
            new { userId, shareId }, ct);

        var (plan, changed) = await ReselectAsync(room, ct);
        await AnnouncePlanAsync(room, plan, changed, ct, force: true);
        return room;
    }

    /// <summary>The tracks in a subscribe request that nobody in the room is publishing.</summary>
    public async Task<IReadOnlyList<string>> FindStaleSubscriptionsAsync(
        VoiceRoomKey key, IEnumerable<VoiceTrackRef> tracks, CancellationToken ct = default)
    {
        var subscribes = tracks.Where(t => t.Direction == VoiceTrackDirection.Subscribe).ToList();
        if (subscribes.Count == 0) return [];

        var room = await rooms.LoadAsync(key, ct);
        if (room is null) return subscribes.Select(t => t.TrackName ?? "?").ToList();

        // Keyed by share, but carrying the track names, because a share is not all-or-nothing: the
        // video half can die while the audio half is still live, and a check that only asked
        // whether the *share* existed would go on accepting subscribes for the dead half of it.
        var liveShares = room.Participants
            .SelectMany(p => p.ActiveScreenShares)
            .ToDictionary(s => s.ShareId, s => s.TrackNames, StringComparer.Ordinal);

        var publishingSessions = room.Participants
            .Where(p => p.PublishState == VoicePublishState.Publishing)
            .Select(p => p.MediaSessionId!)
            .ToHashSet(StringComparer.Ordinal);

        var stale = new List<string>();
        foreach (var track in subscribes)
        {
            if (track.TrackName is not { } name) continue;
            var described = TrackNaming.Describe(name);

            var missing = described.ShareId is { } shareId
                // A share that lists no tracks at all is one nothing can judge, so it is let
                // through rather than reported stale on a technicality.
                ? !liveShares.TryGetValue(shareId, out var names)
                  || (names.Count > 0 && !names.Contains(name))
                : TrackNaming.IsMicrophone(name)
                  && (track.MediaSessionId is null || !publishingSessions.Contains(track.MediaSessionId));

            if (missing) stale.Add(name);
        }

        return stale;
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

    /// <summary>
    /// The room's own ceilings: the guild-scoped keys, clamped by the operator's, with the
    /// configured publisher cap composed in.
    /// </summary>
    private async Task<ResolvedVoiceLimits> ResolveLimitsAsync(string? guildId, CancellationToken ct)
    {
        try
        {
            return await ResolveLimitsCoreAsync(guildId, ct);
        }
        catch (Exception ex)
        {
            // Fail open, and only in this direction.
            logger?.LogWarning(
                ex, "Could not resolve voice limits for guild {Guild} - nothing is enforced", guildId);
            return ResolvedVoiceLimits.Unresolved(Options);
        }
    }

    private async Task<ResolvedVoiceLimits> ResolveLimitsCoreAsync(string? guildId, CancellationToken ct)
    {
        var guild = entitlements is null || guildId is null
            ? EntitlementSet.Empty
            : await entitlements.ResolveAsync(EntitlementSubject.ForGuild(guildId), ct);

        var (participants, participantsCause) = Numeric(guild, EntitlementKeys.VoiceMaxParticipants);
        var (publishers, publishersCause) = Numeric(guild, EntitlementKeys.VoiceMaxPublishers);

        // The configured cap is not replaced by the entitlement, it composes with it, and the lower
        // wins - because it is an operator ceiling in everything but name: a number somebody set
        // about what this box will carry.
        var configured = (long)Math.Max(0, Options.MaxVideoPublishers);
        if (configured < publishers)
        {
            publishers = configured;
            publishersCause = VoiceCause.Operator;
        }

        var key = EntitlementKeys.VoiceVideoCeiling;
        var entitled = guild.Value(key);
        var ceiling = _ceilings.Clamp(key, entitled);
        var ceilingCause = _ceilings.Binds(key, entitled) ? VoiceCause.Operator : VoiceCause.GuildPlan;

        return new ResolvedVoiceLimits(
            new VoiceRoomLimits(participants, key.Ladder!.RungAt(ceiling.AsRank), publishers),
            participantsCause, ceilingCause, publishersCause);
    }

    /// <summary>A guild-scoped numeric ceiling, and whether the plan or this box set it.</summary>
    private (long Value, VoiceCause Cause) Numeric(EntitlementSet guild, EntitlementKey key)
    {
        var entitled = guild.Value(key);

        return _ceilings.Binds(key, entitled)
            ? (_ceilings.Clamp(key, entitled).AsNumber, VoiceCause.Operator)
            : (entitled.AsNumber, VoiceCause.GuildPlan);
    }

    /// <summary>
    /// The publish ceiling for one member of one room: the paired key resolved, or <c>none</c> when
    /// they are past the room's capacity.
    /// </summary>
    private async Task<VoiceCeiling> ResolveVideoCeilingAsync(
        VoiceRoom room, string userId, ResolvedVoiceLimits limits, CancellationToken ct)
    {
        var ladder = EntitlementLadders.VideoQuality;

        // Over capacity is audio-only, and it reports the capacity limit rather than the quality
        // one: raising a room's video ceiling would change nothing at all for the eleventh member of
        // a ten-person room, so pointing them at that upgrade would be a lie.
        if (PositionOf(room, userId) > limits.Limits.MaxParticipants)
        {
            return new VoiceCeiling(ladder.LowestRank, limits.Participants);
        }

        var key = EntitlementKeys.VoiceVideoCeiling;
        var mine = EntitlementSet.Empty;

        try
        {
            if (entitlements is not null)
                mine = await entitlements.ResolveAsync(EntitlementSubject.ForUser(userId), ct);
        }
        catch (Exception ex)
        {
            // Same direction as everywhere else: an unanswerable user side means their ceiling is
            // the key's default, which is unlimited, so the guild's is what applies.
            logger?.LogWarning(ex, "Could not resolve the user side of the voice video ceiling for {User}", userId);
        }

        var guildRank = ladder.RankOf(limits.Limits.VideoCeiling);
        var userRank = mine.Value(key).AsRank;

        if (userRank < guildRank)
        {
            return new VoiceCeiling(userRank, VoiceCause.Paired(EntitlementBoundBy.User));
        }

        // The guild is the lower side, or the two agree.
        return new VoiceCeiling(
            guildRank,
            limits.VideoCeiling.Reason == EntitlementDegradationReason.OperatorCeiling || userRank == guildRank
                ? limits.VideoCeiling
                : VoiceCause.Paired(EntitlementBoundBy.Guild));
    }

    /// <summary>Whether this participant may hold one of the room's video publisher slots. Somebody
    /// already distributing video always may, so adding a second track to a share they already have
    /// is never what the cap catches.</summary>
    private static bool HasPublisherSlot(VoiceRoom room, string userId, long cap)
    {
        var publishers = room.Participants
            .Where(VoiceSubscriptionPlanner.HasVideo)
            .Select(p => p.UserId)
            .ToList();

        return publishers.Contains(userId, StringComparer.Ordinal) || publishers.Count < cap;
    }

    /// <summary>
    /// One participant's place in join order, one-based, or zero when they are not in the room.
    /// </summary>
    private static int PositionOf(VoiceRoom room, string userId) =>
        room.Participants
            .OrderBy(p => p.JoinedAt)
            .ThenBy(p => p.UserId, StringComparer.Ordinal)
            .Select((p, index) => (p.UserId, Place: index + 1))
            .FirstOrDefault(entry => string.Equals(entry.UserId, userId, StringComparison.Ordinal))
            .Place;

    /// <summary>Whose limit it was, so a call to action links at the thing an upgrade would apply to
    /// rather than at whichever subject the request mentioned. A direct call has no guild behind it,
    /// so its limits can only ever be the caller's own or the operator's.</summary>
    private static EntitlementSubject SubjectOf(VoiceRoom room, string userId, string? boundBy) =>
        room.GuildId is { } guildId && boundBy != EntitlementBoundBy.User
            ? EntitlementSubject.ForGuild(guildId)
            : EntitlementSubject.ForUser(userId);

    private readonly record struct VoiceCeiling(int Rank, VoiceCause Cause);

    /// <summary>Re-ranks after a roster change, or answers all-to-all when planning is not
    /// registered.</summary>
    private Task<(VoiceSubscriptionPlan Plan, bool Changed)> ReselectAsync(
        VoiceRoom room, CancellationToken ct) =>
        subscriptions is null
            ? Task.FromResult((VoiceSubscriptionPlan.Unplanned, false))
            : subscriptions.ReselectAsync(room, ct);

    /// <summary>Pushes the plan to the room, but only when the ranked set actually moved.</summary>
    private Task AnnouncePlanAsync(
        VoiceRoom room, VoiceSubscriptionPlan plan, bool changed, CancellationToken ct,
        bool force = false) =>
        (changed || force) && plan.IsSelective
            ? announcer.SendSubscriptionsAsync(room, plan, ct)
            : Task.CompletedTask;
}

/// <summary>
/// The ceilings in force in one room, as they are stored on the roster and read off the snapshot.
/// </summary>
/// <param name="MaxParticipants">
/// <see cref="EntitlementValue.Unlimited"/> when nothing caps the room.
/// </param>
/// <param name="VideoCeiling">
/// A rung name of <see cref="EntitlementLadders.VideoQuality"/>, stored by name rather than by rank
/// because the rung list is what the whole system agrees on and a stored index would quietly mean
/// something else the day a rung is added in the middle.
/// </param>
/// <param name="MaxPublishers">The lower of the guild's entitlement and the configured cap.</param>
public sealed record VoiceRoomLimits(long MaxParticipants, string VideoCeiling, long MaxPublishers)
{
    public int VideoCeilingRank => EntitlementLadders.VideoQuality.RankOf(VideoCeiling);

    /// <param name="publisherCount">Room state rather than entitlement state, and on the wire because
    /// a cap without a count is unrenderable: "2 of 2 people are sharing" is a sentence, "you cannot
    /// share" is a mystery.</param>
    public VoiceRoomLimitsDto ToDto(int publisherCount) => new(
        EntitlementValueDto.Number(MaxParticipants),
        EntitlementValueDto.OnLadder(EntitlementLadders.VideoQuality, VideoCeilingRank),
        EntitlementValueDto.Number(MaxPublishers),
        publisherCount);
}

/// <summary>Which side set a limit, in the closed reason vocabulary, and which subject that side is.
/// <see cref="BoundBy"/> is null exactly when the reason is an operator ceiling, which is not a
/// subject anybody can upgrade.</summary>
public readonly record struct VoiceCause(EntitlementDegradationReason Reason, string? BoundBy)
{
    public static readonly VoiceCause Operator =
        new(EntitlementDegradationReason.OperatorCeiling, null);

    public static readonly VoiceCause GuildPlan =
        new(EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild);

    /// <summary>Both sides carried a ceiling and the lower won.</summary>
    public static VoiceCause Paired(string boundBy) =>
        new(EntitlementDegradationReason.PairedCeiling, boundBy);
}

/// <summary>
/// One reduction a voice limit caused, in the terms the enforcement site decided them.
/// </summary>
public sealed record VoiceDegradation(
    EntitlementKey Key,
    EntitlementValue Requested,
    EntitlementValue Granted,
    VoiceCause Cause,
    EntitlementSubject Subject)
{
    /// <param name="instanceSellsUpgrades">False on a self-hosted instance and on a hosted one whose
    /// billing is not configured. Both render as a sentence with no button.</param>
    /// <param name="actorCanManageGuild">Whether this caller could actually buy the guild's upgrade.
    /// Ignored for a user-side limit, where the caller is the person who would upgrade.</param>
    public EntitlementDegradationDto Describe(bool instanceSellsUpgrades, bool actorCanManageGuild) =>
        EntitlementDegradationDto.From(
            Key, Requested, Granted, Cause.Reason, Subject,
            EntitlementRemedyPolicy.For(
                Cause.Reason, Cause.BoundBy, instanceSellsUpgrades, actorCanManageGuild),
            Cause.BoundBy);
}

/// <summary>The room's ceilings plus which side set each of them.</summary>
/// <param name="Resolved">False when the resolver could not answer.</param>
public sealed record ResolvedVoiceLimits(
    VoiceRoomLimits Limits,
    VoiceCause Participants,
    VoiceCause VideoCeiling,
    VoiceCause Publishers,
    bool Resolved = true)
{
    /// <summary>
    /// What applies when nothing could be resolved: no participant cap, the top of the video
    /// ladder, and the configured publisher cap.
    /// </summary>
    public static ResolvedVoiceLimits Unresolved(VoiceSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var ladder = EntitlementLadders.VideoQuality;

        return new ResolvedVoiceLimits(
            new VoiceRoomLimits(
                EntitlementValue.Unlimited,
                ladder.RungAt(ladder.HighestRank),
                Math.Max(0, options.MaxVideoPublishers)),
            VoiceCause.GuildPlan,
            VoiceCause.GuildPlan,
            VoiceCause.Operator,
            false);
    }
}

/// <summary>A join that happened, and what it cost the joiner.</summary>
/// <param name="Position">One-based place in join order.</param>
public sealed record VoiceAdmission(
    VoiceRoom Room,
    VoiceRoomLimits Limits,
    int Position,
    VoiceDegradation? OverCapacity)
{
    /// <summary>The degradations for the response body, empty when nothing was reduced - which is
    /// what a client must treat as the normal case, and what makes the reply byte-identical to what a
    /// v1 client already receives.</summary>
    public IReadOnlyList<EntitlementDegradationDto> Describe(
        bool instanceSellsUpgrades, bool actorCanManageGuild) =>
        OverCapacity is null ? [] : [OverCapacity.Describe(instanceSellsUpgrades, actorCanManageGuild)];
}

/// <summary>What the plan says about one subscribe request.</summary>
/// <param name="Unplanned">
/// Track names this subscriber's set does not include, empty unless the plan is both selective and
/// binding.
/// </param>
/// <param name="Tracks">
/// The request's tracks with the simulcast layer filled in, or the caller's own list unchanged when
/// there is nothing to say.
/// </param>
public sealed record VoiceSubscribeDecision(
    IReadOnlyList<string> Unplanned,
    IReadOnlyList<VoiceTrackRef> Tracks);

/// <summary>What a publisher intends to send.</summary>
public readonly record struct VoiceVideoRequest(int Height, int Framerate)
{
    /// <summary>Whatever the room will allow, for a caller that has not been told a size.</summary>
    public static readonly VoiceVideoRequest Best = new(int.MaxValue, int.MaxValue);

    /// <summary>
    /// The height the publisher actually committed to, or zero when they committed to nothing.
    /// </summary>
    public int DeclaredHeight => Height is <= 0 or int.MaxValue ? 0 : Height;
}

/// <summary>What a publisher says it intends to send, as it arrives on a negotiate body.</summary>
public sealed record VoiceVideoIntent(int Height = 0, int Framerate = 0)
{
    public VoiceVideoRequest ToRequest() => new(
        Height <= 0 ? int.MaxValue : Height,
        Framerate <= 0 ? int.MaxValue : Framerate);

    /// <summary>The request an optional body field describes, with absent reading as
    /// <see cref="VoiceVideoRequest.Best"/>. One place, so the two media controllers cannot disagree
    /// about what a missing <c>video</c> means.</summary>
    public static VoiceVideoRequest RequestOf(VoiceVideoIntent? intent) =>
        intent?.ToRequest() ?? VoiceVideoRequest.Best;
}

/// <summary>What <see cref="VoiceRoomService.ReviseVideoLayerAsync"/> did.</summary>
/// <param name="Changed">Whether the recorded cap actually moved, in either direction.</param>
/// <param name="MaxLayer">The cap now in force, null when nothing binds this publisher.</param>
public readonly record struct VoiceLayerRevision(bool Changed, VoiceVideoLayer? MaxLayer)
{
    /// <summary>Nothing was declared, nothing was published, or the ceiling could not be resolved.
    /// Whatever cap the roster held is untouched - which is not the same claim as "no cap", and is
    /// why this reports <see cref="Changed"/> false rather than a layer of its own.</summary>
    public static readonly VoiceLayerRevision Undeclared = new(false, null);
}

/// <summary>What a publisher may actually send.</summary>
/// <param name="Rung">The granted rung.</param>
/// <param name="Height">The request clamped to <paramref name="Rung"/>.</param>
/// <param name="MaxLayer">
/// The best simulcast layer of this publish that may be distributed, or null when nothing caps it -
/// which is every publish inside its ceiling and every publish that declared no size.
/// </param>
public sealed record VoicePublishDecision(
    bool VideoAllowed,
    string Rung,
    int Height,
    int Framerate,
    VoiceRoomLimits Limits,
    int PublisherCount,
    IReadOnlyList<VoiceDegradation> Degradations,
    VoiceDegradation? Refusal,
    VoiceVideoLayer? MaxLayer = null)
{
    public IReadOnlyList<EntitlementDegradationDto> Describe(
        bool instanceSellsUpgrades, bool actorCanManageGuild) =>
        Degradations
            .Select(degradation => degradation.Describe(instanceSellsUpgrades, actorCanManageGuild))
            .ToList();

    /// <summary>The refusal body, or null when the publish was allowed.</summary>
    public EntitlementDenialDto? Denial(bool instanceSellsUpgrades, bool actorCanManageGuild) =>
        Refusal is null
            ? null
            : EntitlementDenialDto.From(Refusal.Describe(instanceSellsUpgrades, actorCanManageGuild));

    internal static VoicePublishDecision Refused(
        VoiceRoomLimits limits, int publisherCount, VoiceDegradation refusal) =>
        new(false, EntitlementLadders.VideoQuality.RungAt(EntitlementLadders.VideoQuality.LowestRank),
            0, 0, limits, publisherCount, [], refusal);

    /// <summary>The answer for a room the server does not have, which is the same answer
    /// <see cref="VoiceRoomService.CanPublishVideoAsync"/> gives and for the same reason: refusing a
    /// publish into a room that has not been created yet would fail the first publisher of every
    /// call.</summary>
    internal static VoicePublishDecision Unconstrained(VoiceRoomLimits limits, VoiceVideoRequest request)
    {
        var (height, framerate) = VideoRungs.Clamp(limits.VideoCeiling, request.Height, request.Framerate);
        return new VoicePublishDecision(true, limits.VideoCeiling, height, framerate, limits, 0, [], null);
    }
}
