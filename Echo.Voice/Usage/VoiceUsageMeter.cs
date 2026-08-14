using Echo.Voice.Rooms;
using Echo.Voice.Tracks;
using Microsoft.Extensions.Logging;

namespace Echo.Voice.Usage;

/// <summary>
/// Per-guild voice usage accounting, sampled off the heartbeat that <see cref="VoiceReconciler"/>
/// already processes.
/// </summary>
public sealed class VoiceUsageMeter(
    IVoiceUsageBackend backend,
    TimeProvider clock,
    ILogger<VoiceUsageMeter> logger)
{
    /// <summary>How often one room is sampled, whatever the heartbeat rate happens to be.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);

    /// <summary>The longest gap the meter will credit between two samples of the same room. Also
    /// the mark's own expiry, so a gap larger than this is indistinguishable from a first sample
    /// and is treated as one.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(4 * 60);

    /// <summary>How long a day's counters live.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Where direct-call usage is bucketed.</summary>
    public const string DirectScope = "@direct";

    public const string SubscriberSecondsPrefix = "sub_seconds:";
    public const string TrackSecondsPrefix = "track_seconds:";
    public const string ParticipantSecondsField = "participant_seconds";
    public const string SamplesField = "samples";

    public static string DayHashKey(string scope, DateOnly day) =>
        $"voice:usage:day:{scope}:{day:yyyyMMdd}";

    public static string ScopeIndexKey(DateOnly day) => $"voice:usage:scopes:{day:yyyyMMdd}";

    public static string LeaseKey(VoiceRoomKey key) => $"voice:usage:lease:{key.Kind}:{key.Id}";

    public static string MarkKey(VoiceRoomKey key) => $"voice:usage:mark:{key.Kind}:{key.Id}";

    /// <summary>The scope a room's usage is recorded against.</summary>
    public static string ScopeOf(VoiceRoom room) =>
        string.IsNullOrWhiteSpace(room.GuildId) ? DirectScope : room.GuildId!;

    /// <summary>What one room is currently distributing.</summary>
    public static VoiceRoomFanout Measure(VoiceRoom room) => Measure(room, null);

    /// <summary>
    /// What one room is currently distributing, counted against a subscription plan.
    /// </summary>
    public static VoiceRoomFanout Measure(VoiceRoom room, VoiceSubscriptionPlan? plan)
    {
        var fanout = new VoiceRoomFanout { Participants = room.Participants.Count };

        // Everyone except the publisher.
        var peers = room.Participants.Count - 1;
        if (peers <= 0) return fanout;

        if (plan is { IsSelective: true }) return MeasurePlanned(room, plan, fanout);

        foreach (var participant in room.Participants)
        {
            if (participant.PublishState == VoicePublishState.Publishing)
                fanout.AddTrack(VoiceUsageTrackKind.Audio, peers);

            foreach (var video in participant.ActiveVideoTracks)
                fanout.AddTrack(
                    VoiceUsageTrackKinds.FromTrackKind(TrackNaming.Describe(video.TrackName).Kind), peers);

            foreach (var share in participant.ActiveScreenShares)
            foreach (var trackName in share.TrackNames)
                fanout.AddTrack(
                    VoiceUsageTrackKinds.FromTrackKind(TrackNaming.Describe(trackName).Kind), peers);
        }

        return fanout;
    }

    /// <summary>
    /// Counts each published track once, against however many subscribers the plan gives it.
    /// </summary>
    private static VoiceRoomFanout MeasurePlanned(
        VoiceRoom room, VoiceSubscriptionPlan plan, VoiceRoomFanout fanout)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var set in plan.Sets.Values)
        foreach (var subscription in set.Tracks)
        {
            var key = subscription.MatchKey();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var participant in room.Participants)
        {
            if (participant.PublishState == VoicePublishState.Publishing)
                fanout.AddTrack(
                    VoiceUsageTrackKind.Audio,
                    counts.GetValueOrDefault(VoiceSubscription.MatchKey(
                        participant.MediaSessionId, participant.AudioTrackName!)));

            foreach (var video in participant.ActiveVideoTracks)
                fanout.AddTrack(
                    VoiceUsageTrackKinds.FromTrackKind(TrackNaming.Describe(video.TrackName).Kind),
                    counts.GetValueOrDefault(VoiceSubscription.MatchKey(
                        video.MediaSessionId ?? participant.MediaSessionId, video.TrackName)));

            foreach (var share in participant.ActiveScreenShares)
            foreach (var trackName in share.TrackNames)
                fanout.AddTrack(
                    VoiceUsageTrackKinds.FromTrackKind(TrackNaming.Describe(trackName).Kind),
                    counts.GetValueOrDefault(VoiceSubscription.MatchKey(
                        share.MediaSessionId ?? participant.MediaSessionId, trackName)));
        }

        return fanout;
    }

    /// <summary>Offers one room for sampling.</summary>
    public Task SampleAsync(VoiceRoom room, CancellationToken ct = default) =>
        SampleAsync(room, null, ct);

    /// <summary>
    /// Offers one room for sampling, counted against the subscription plan it is being distributed
    /// under.
    /// </summary>
    public async Task SampleAsync(VoiceRoom room, VoiceSubscriptionPlan? plan, CancellationToken ct)
    {
        try
        {
            var fanout = Measure(room, plan);
            // Checked before the lease is claimed, so an idle room neither writes nor consumes its
            // sampling slot: the first interval in which it becomes busy is then the one that
            // establishes the mark.
            if (fanout.CostsNothing) return;

            if (!await backend.TryClaimAsync(LeaseKey(room.Key), SampleInterval, ct)) return;

            var now = clock.GetUtcNow();
            var nowSeconds = now.ToUnixTimeSeconds();
            var mark = await backend.ReadCounterAsync(MarkKey(room.Key), ct);
            await backend.WriteCounterAsync(MarkKey(room.Key), nowSeconds, MaxGap, ct);
            if (mark is null) return;

            var elapsed = nowSeconds - mark.Value;
            if (elapsed <= 0) return;
            if (elapsed > (long)MaxGap.TotalSeconds) elapsed = (long)MaxGap.TotalSeconds;

            // Attributed whole to the day the sample landed in rather than split across midnight.
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            var scope = ScopeOf(room);

            var deltas = new List<KeyValuePair<string, long>>(fanout.Subscribers.Count + 2)
            {
                new(ParticipantSecondsField, fanout.Participants * elapsed),
                new(SamplesField, 1),
            };
            foreach (var (kind, subscribers) in fanout.Subscribers)
                deltas.Add(new KeyValuePair<string, long>(
                    SubscriberSecondsPrefix + VoiceUsageTrackKinds.Name(kind), subscribers * elapsed));
            foreach (var (kind, tracks) in fanout.Tracks)
                deltas.Add(new KeyValuePair<string, long>(
                    TrackSecondsPrefix + VoiceUsageTrackKinds.Name(kind), tracks * elapsed));

            await backend.AccumulateAsync(DayHashKey(scope, day), deltas, Retention, ct);
            await backend.AddToSetAsync(ScopeIndexKey(day), scope, Retention, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Voice usage sampling failed for room {Room} - measurement lost", room.Key);
        }
    }

    /// <summary>One scope's usage for one UTC day.</summary>
    public Task<VoiceUsageTotals> GetDayAsync(string scope, DateOnly day, CancellationToken ct = default) =>
        GetRangeAsync(scope, day, day, ct);

    /// <summary>One scope's usage summed over an inclusive UTC day range.</summary>
    public async Task<VoiceUsageTotals> GetRangeAsync(
        string scope, DateOnly from, DateOnly through, CancellationToken ct = default)
    {
        if (through < from) (from, through) = (through, from);

        var subscriberSeconds = new Dictionary<VoiceUsageTrackKind, long>();
        var trackSeconds = new Dictionary<VoiceUsageTrackKind, long>();
        long participantSeconds = 0;
        long samples = 0;

        for (var day = from; day <= through; day = day.AddDays(1))
        {
            var fields = await backend.ReadHashAsync(DayHashKey(scope, day), ct);
            foreach (var (field, value) in fields)
            {
                if (field == ParticipantSecondsField) participantSeconds += value;
                else if (field == SamplesField) samples += value;
                else if (field.StartsWith(SubscriberSecondsPrefix, StringComparison.Ordinal))
                    Add(subscriberSeconds, field[SubscriberSecondsPrefix.Length..], value);
                else if (field.StartsWith(TrackSecondsPrefix, StringComparison.Ordinal))
                    Add(trackSeconds, field[TrackSecondsPrefix.Length..], value);
            }
        }

        return new VoiceUsageTotals(
            scope, from, through, subscriberSeconds, trackSeconds, participantSeconds, samples);

        static void Add(Dictionary<VoiceUsageTrackKind, long> into, string kindName, long value)
        {
            // An unrecognised kind is one written by a newer build against the same Redis.
            if (VoiceUsageTrackKinds.Parse(kindName) is not { } kind) return;
            into[kind] = into.GetValueOrDefault(kind) + value;
        }
    }

    /// <summary>Every scope that recorded usage on a given UTC day - the enumeration the pricing
    /// and abuse questions both start from.</summary>
    public Task<IReadOnlyList<string>> GetActiveScopesAsync(DateOnly day, CancellationToken ct = default) =>
        backend.ReadSetAsync(ScopeIndexKey(day), ct);
}
