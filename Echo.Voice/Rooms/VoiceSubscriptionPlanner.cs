using Echo.Voice.Tracks;

namespace Echo.Voice.Rooms;

/// <summary>Decides who pulls what.</summary>
public static class VoiceSubscriptionPlanner
{
    /// <summary>Not speaking and not recently.</summary>
    private const int TierSilent = 0;

    /// <summary>Spoke within <see cref="VoiceSubscriptionOptions.SpeakerHoldTime"/>.</summary>
    private const int TierRecent = 1;

    /// <summary>Speaking right now.</summary>
    private const int TierSpeaking = 2;

    /// <summary>Re-ranks the active set in place, returning whether it changed.</summary>
    public static bool Select(
        VoiceRoom room, VoiceAttention attention, VoiceSubscriptionOptions options, DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        attention.SelectedAtUnixMs = nowMs;

        var candidates = room.Participants
            .Where(p => p.PublishState == VoicePublishState.Publishing)
            .ToList();

        var tiers = candidates.ToDictionary(
            p => p.UserId, p => Tier(attention, p.UserId, options, nowMs), StringComparer.Ordinal);

        var ranked = candidates
            .OrderByDescending(p => tiers[p.UserId])
            .ThenByDescending(p => attention.Speakers.TryGetValue(p.UserId, out var s) ? s.LastSpokeAtUnixMs : 0)
            .ThenBy(p => p.JoinedAt)
            .ThenBy(p => p.UserId, StringComparer.Ordinal)
            .ToList();

        var capacity = Math.Max(1, options.ActiveSpeakerCount);
        var hardCap = Math.Max(capacity, options.MaxActiveSpeakers);

        var selected = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var participant in ranked)
        {
            if (selected.Count >= hardCap) break;
            if (tiers[participant.UserId] != TierSpeaking) continue;
            if (taken.Add(participant.UserId)) selected.Add(participant.UserId);
        }

        var incumbents = attention.ActiveSpeakers
            .Where(s => tiers.ContainsKey(s.UserId))
            .ToList();

        foreach (var incumbent in incumbents)
        {
            if (selected.Count >= capacity) break;
            if (taken.Contains(incumbent.UserId)) continue;

            var holds = tiers[incumbent.UserId] >= TierRecent;
            var dwelling = nowMs - incumbent.EnteredAtUnixMs
                           < (long)options.MinimumDwell.TotalMilliseconds;
            if (!holds && !dwelling) continue;

            taken.Add(incumbent.UserId);
            selected.Add(incumbent.UserId);
        }

        foreach (var participant in ranked)
        {
            if (selected.Count >= capacity) break;
            if (tiers[participant.UserId] < TierRecent) continue;
            if (taken.Add(participant.UserId)) selected.Add(participant.UserId);
        }

        if (selected.Count == 0)
        {
            foreach (var incumbent in incumbents)
            {
                if (selected.Count >= capacity) break;
                if (taken.Add(incumbent.UserId)) selected.Add(incumbent.UserId);
            }
        }

        var previous = attention.ActiveSpeakers.ToDictionary(
            s => s.UserId, s => s.EnteredAtUnixMs, StringComparer.Ordinal);

        var changed = previous.Count != selected.Count || selected.Any(id => !previous.ContainsKey(id));

        attention.ActiveSpeakers = selected
            .Select(id => new VoiceActiveSpeaker
            {
                UserId = id,
                // Carried over rather than restamped, or an entry would reset its own dwell clock
                // on every recomputation and could then never be displaced.
                EnteredAtUnixMs = previous.TryGetValue(id, out var entered) ? entered : nowMs,
            })
            .ToList();

        if (changed) attention.Revision++;
        return changed;
    }

    /// <summary>
    /// Turns a roster, a selection and each subscriber's own preferences into concrete track lists.
    /// </summary>
    public static VoiceSubscriptionPlan Build(
        VoiceRoom room, VoiceAttention attention, VoiceSubscriptionOptions options)
    {
        var participants = room.Participants;
        var present = participants.Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);

        var activeSpeakers = attention.ActiveSpeakers
            .Select(s => s.UserId)
            .Where(present.Contains)
            .ToList();

        // Selective only when there is both a reason and a basis for it.
        var selective = options.Enabled
                        && participants.Count > options.ActiveSpeakerThreshold
                        && activeSpeakers.Count > 0;

        var mode = selective ? VoiceSubscriptionMode.ActiveSpeaker : VoiceSubscriptionMode.All;

        // Join order, so the cap is answered the same way every time it is asked.
        var videoPublishers = participants
            .Where(HasVideo)
            .OrderBy(p => p.JoinedAt)
            .ThenBy(p => p.UserId, StringComparer.Ordinal)
            .Take(Math.Max(0, options.MaxVideoPublishers))
            .Select(p => p.UserId)
            .ToList();

        var distributedVideo = videoPublishers.ToHashSet(StringComparer.Ordinal);
        var sets = new Dictionary<string, VoiceSubscriptionSet>(StringComparer.Ordinal);

        // A publisher whose video the cap refused is a restriction whatever the room size is.
        var restricted = participants.Count(HasVideo) > videoPublishers.Count;

        foreach (var subscriber in participants)
        {
            var preferences = attention.Subscribers.GetValueOrDefault(subscriber.UserId)
                              ?? new VoiceSubscriberState();

            var pinned = preferences.Pinned
                .Where(present.Contains)
                .Take(Math.Max(0, options.MaxPinnedPerSubscriber))
                .ToHashSet(StringComparer.Ordinal);

            var audible = selective
                ? activeSpeakers.Concat(pinned).ToHashSet(StringComparer.Ordinal)
                : present;

            var tracks = new List<VoiceSubscription>();

            foreach (var publisher in participants)
            {
                if (publisher.UserId == subscriber.UserId) continue;

                if (publisher.PublishState == VoicePublishState.Publishing
                    && audible.Contains(publisher.UserId))
                {
                    tracks.Add(new VoiceSubscription(
                        publisher.UserId, publisher.MediaSessionId!, publisher.AudioTrackName!,
                        TrackNaming.AudioKind, null, null));
                }

                if (!distributedVideo.Contains(publisher.UserId)) continue;

                var tilePaused = preferences.IsPaused
                                 || preferences.PausedPublishers.Contains(publisher.UserId, StringComparer.Ordinal);

                restricted |= tilePaused
                              && (publisher.ActiveVideoTracks.Count > 0
                                  || publisher.ActiveScreenShares.Count > 0);

                if (!tilePaused)
                {
                    foreach (var video in publisher.ActiveVideoTracks)
                        tracks.Add(new VoiceSubscription(
                            publisher.UserId,
                            video.MediaSessionId ?? publisher.MediaSessionId,
                            video.TrackName,
                            TrackNaming.VideoKind,
                            null,
                            VoiceVideoLayers.Name(LayerFor(
                                preferences, publisher.UserId, TrackNaming.VideoKind, selective, options))));
                }

                foreach (var share in publisher.ActiveScreenShares)
                foreach (var trackName in share.TrackNames)
                {
                    var described = TrackNaming.Describe(trackName);
                    var isAudio = described.Kind == TrackNaming.ScreenAudioKind;

                    // A collapsed tile stops paying for pixels, not for sound.
                    if (!isAudio && tilePaused) continue;

                    if (isAudio
                        && options.ScreenShareAudioOptIn
                        && !preferences.ScreenAudioShares.Contains(share.ShareId, StringComparer.Ordinal))
                    {
                        restricted = true;
                        continue;
                    }

                    tracks.Add(new VoiceSubscription(
                        publisher.UserId,
                        share.MediaSessionId ?? publisher.MediaSessionId!,
                        trackName,
                        described.Kind,
                        share.ShareId,
                        isAudio ? null : VoiceVideoLayers.Name(
                            LayerFor(preferences, publisher.UserId, described.Kind, selective, options))));
                }
            }

            sets[subscriber.UserId] = new VoiceSubscriptionSet(subscriber.UserId, tracks);
        }

        return new VoiceSubscriptionPlan(
            mode, attention.Revision, activeSpeakers, videoPublishers, sets, restricted);
    }

    /// <summary>The simulcast layer one subscriber should pull one publisher at.</summary>
    public static VoiceVideoLayer LayerFor(
        VoiceSubscriberState preferences, string publisherUserId, string trackKind,
        bool selective, VoiceSubscriptionOptions options)
    {
        if (preferences.TileHeights.TryGetValue(publisherUserId, out var height) && height > 0)
        {
            if (height <= options.LowLayerMaxHeight) return VoiceVideoLayer.Low;
            if (height <= options.MediumLayerMaxHeight) return VoiceVideoLayer.Medium;
            return VoiceVideoLayer.High;
        }

        return selective && trackKind == TrackNaming.VideoKind
            ? options.GridLayer
            : options.DefaultLayer;
    }

    /// <summary>Whether this participant is distributing anything that the video publisher cap
    /// applies to. Camera counts: it is the more expensive half of the video bill, not the cheap
    /// one.</summary>
    public static bool HasVideo(VoiceParticipant participant) =>
        participant.ActiveScreenShares.Count > 0 || participant.ActiveVideoTracks.Count > 0;

    private static int Tier(
        VoiceAttention attention, string userId, VoiceSubscriptionOptions options, long nowMs)
    {
        if (!attention.Speakers.TryGetValue(userId, out var speaker)) return TierSilent;

        if (speaker.IsSpeaking) return TierSpeaking;

        if (speaker.LastSpokeAtUnixMs > 0
            && nowMs - speaker.LastSpokeAtUnixMs <= (long)options.SpeakerHoldTime.TotalMilliseconds)
            return TierRecent;

        return TierSilent;
    }
}
