using Microsoft.Extensions.Logging;

namespace Echo.Voice.Rooms;

/// <summary>What one subscriber is telling the server about its own rendering.</summary>
/// <param name="Paused">The whole client is backgrounded or hidden.</param>
/// <param name="Pinned">Publishers to keep subscribed regardless of ranking.</param>
/// <param name="PausedPublishers">Publishers whose tile is collapsed.</param>
/// <param name="TileHeights">Publisher user id to rendered tile height in device pixels.</param>
/// <param name="ScreenAudioShares">Share ids whose audio half this subscriber wants.</param>
public sealed record VoiceSubscriberUpdate(
    bool? Paused = null,
    IReadOnlyList<string>? Pinned = null,
    IReadOnlyList<string>? PausedPublishers = null,
    IReadOnlyDictionary<string, int>? TileHeights = null,
    IReadOnlyList<string>? ScreenAudioShares = null);

/// <summary>
/// The stateful half of subscription planning: reads and writes <see cref="VoiceAttention"/>, runs
/// <see cref="VoiceSubscriptionPlanner"/> against it, and answers "what should this room be
/// pulling".
/// </summary>
public sealed class VoiceSubscriptions(
    VoiceAttentionStore attention,
    VoiceSubscriptionOptions options,
    TimeProvider clock,
    ILogger<VoiceSubscriptions> logger)
{
    public VoiceSubscriptionOptions Options { get; } = options;

    public VoiceAttentionStore Attention { get; } = attention;

    /// <summary>The current plan for a room, without re-ranking anything.</summary>
    public async Task<VoiceSubscriptionPlan> PlanAsync(VoiceRoom room, CancellationToken ct = default)
    {
        if (!NeedsPlan(room)) return VoiceSubscriptionPlan.Unplanned;

        try
        {
            var state = await Attention.LoadAsync(room.Key, ct);
            return VoiceSubscriptionPlanner.Build(room, state, Options);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read voice attention for room {Room} - falling back to all-to-all", room.Key);
            return VoiceSubscriptionPlan.Unplanned;
        }
    }

    /// <summary>Records a speech transition and re-ranks if it moved anything.</summary>
    public Task<(VoiceSubscriptionPlan Plan, bool Changed)> RecordSpeakingAsync(
        VoiceRoom room, string userId, bool isSpeaking, CancellationToken ct = default) =>
        MutateAsync(room, state =>
        {
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var speaker = state.Speaker(userId);
            var minimum = (long)Options.MinimumSpeechToHold.TotalMilliseconds;

            if (isSpeaking)
            {
                // Only the onset moves the clock.
                if (!speaker.IsSpeaking || speaker.SpeakingSinceUnixMs == 0)
                {
                    speaker.IsSpeaking = true;
                    speaker.SpeakingSinceUnixMs = nowMs;
                }
                else if (nowMs - speaker.SpeakingSinceUnixMs >= minimum)
                {
                    speaker.LastSpokeAtUnixMs = nowMs;
                }
            }
            else
            {
                // The run is credited on the way out, and only if it lasted.
                if (speaker.IsSpeaking
                    && speaker.SpeakingSinceUnixMs > 0
                    && nowMs - speaker.SpeakingSinceUnixMs >= minimum)
                    speaker.LastSpokeAtUnixMs = nowMs;

                speaker.IsSpeaking = false;
                speaker.SpeakingSinceUnixMs = 0;
            }

            return true;
        }, ct);

    /// <summary>Applies one subscriber's reported rendering state.</summary>
    public Task<(VoiceSubscriptionPlan Plan, bool Changed)> UpdateSubscriberAsync(
        VoiceRoom room, string userId, VoiceSubscriberUpdate update, CancellationToken ct = default) =>
        MutateAsync(room, state =>
        {
            var subscriber = state.Subscriber(userId);

            if (update.Paused is { } paused) subscriber.IsPaused = paused;

            if (update.Pinned is { } pinned)
                subscriber.Pinned = pinned
                    .Distinct(StringComparer.Ordinal)
                    .Take(Math.Max(0, Options.MaxPinnedPerSubscriber))
                    .ToList();

            if (update.PausedPublishers is { } pausedPublishers)
                subscriber.PausedPublishers = pausedPublishers.Distinct(StringComparer.Ordinal).ToList();

            if (update.TileHeights is { } tiles)
                subscriber.TileHeights = tiles
                    .Where(pair => pair.Value > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            if (update.ScreenAudioShares is { } shares)
                subscriber.ScreenAudioShares = shares.Distinct(StringComparer.Ordinal).ToList();

            return true;
        }, ct);

    /// <summary>
    /// Re-ranks if the stored selection has gone stale, and prunes anyone who has left.
    /// </summary>
    public async Task<(VoiceSubscriptionPlan Plan, bool Changed)> RefreshAsync(
        VoiceRoom room, CancellationToken ct = default)
    {
        if (!NeedsPlan(room)) return (VoiceSubscriptionPlan.Unplanned, false);

        try
        {
            var state = await Attention.LoadAsync(room.Key, ct);
            if (!IsStale(state)) return (VoiceSubscriptionPlanner.Build(room, state, Options), false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read voice attention for room {Room}", room.Key);
            return (VoiceSubscriptionPlan.Unplanned, false);
        }

        return await MutateAsync(room, state => IsStale(state), ct);
    }

    /// <summary>Re-ranks because the roster moved - somebody joined, published, or stopped
    /// publishing. Unconditional, because the eligible set is exactly the publishing set and any of
    /// those three can change it.</summary>
    public Task<(VoiceSubscriptionPlan Plan, bool Changed)> ReselectAsync(
        VoiceRoom room, CancellationToken ct = default) =>
        MutateAsync(room, _ => true, ct);

    /// <summary>Forgets one participant entirely - they left, so nothing about their attention or
    /// their claim on a ranked slot can still be true.</summary>
    public Task<(VoiceSubscriptionPlan Plan, bool Changed)> ForgetAsync(
        VoiceRoom room, string userId, CancellationToken ct = default) =>
        MutateAsync(room, state =>
        {
            var removed = state.Speakers.Remove(userId) | state.Subscribers.Remove(userId);
            return removed || state.ActiveSpeakers.Any(s => s.UserId == userId);
        }, ct);

    /// <summary>Drops a reaped room's attention.</summary>
    public async Task DropAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        try
        {
            await Attention.DropAsync(key, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not drop voice attention for room {Room} - it expires on its own", key);
        }
    }

    /// <summary>The one write path.</summary>
    private async Task<(VoiceSubscriptionPlan Plan, bool Changed)> MutateAsync(
        VoiceRoom room, Func<VoiceAttention, bool> mutate, CancellationToken ct)
    {
        if (!NeedsPlan(room)) return (VoiceSubscriptionPlan.Unplanned, false);

        try
        {
            var changed = false;
            var now = clock.GetUtcNow();

            var state = await Attention.MutateAsync(room.Key, attentionState =>
            {
                var reason = mutate(attentionState);
                reason |= attentionState.PruneTo(room.AllUserIds());
                if (!reason) return false;

                changed = VoiceSubscriptionPlanner.Select(room, attentionState, Options, now);
                return true;
            }, ct);

            return (VoiceSubscriptionPlanner.Build(room, state, Options), changed);
        }
        catch (Exception ex)
        {
            // Including a lock timeout.
            logger.LogDebug(ex, "Voice subscription planning failed for room {Room}", room.Key);
            return (VoiceSubscriptionPlan.Unplanned, false);
        }
    }

    /// <summary>Whether this room is worth touching Redis for at all.</summary>
    private bool NeedsPlan(VoiceRoom room) =>
        Options.Enabled
        && (room.Participants.Count > Options.ActiveSpeakerThreshold
            || room.Participants.Any(VoiceSubscriptionPlanner.HasVideo));

    private bool IsStale(VoiceAttention state) =>
        clock.GetUtcNow().ToUnixTimeMilliseconds() - state.SelectedAtUnixMs
        >= (long)Options.RecomputeInterval.TotalMilliseconds;
}
