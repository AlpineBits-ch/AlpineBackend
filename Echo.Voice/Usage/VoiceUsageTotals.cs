namespace Echo.Voice.Usage;

/// <summary>What one room was distributing at the instant it was sampled.</summary>
public sealed class VoiceRoomFanout
{
    /// <summary>Concurrent subscribers per kind: for each published track, everyone else in the
    /// room.</summary>
    public Dictionary<VoiceUsageTrackKind, long> Subscribers { get; } = [];

    /// <summary>Live published tracks per kind.</summary>
    public Dictionary<VoiceUsageTrackKind, long> Tracks { get; } = [];

    public int Participants { get; set; }

    public long TotalSubscribers => Subscribers.Values.Sum();

    /// <summary>Nothing is leaving the SFU, so nothing is being billed.</summary>
    public bool CostsNothing => TotalSubscribers == 0;

    internal void AddTrack(VoiceUsageTrackKind kind, long subscribers)
    {
        Tracks[kind] = Tracks.GetValueOrDefault(kind) + 1;
        if (subscribers > 0)
            Subscribers[kind] = Subscribers.GetValueOrDefault(kind) + subscribers;
    }
}

/// <summary>Accumulated usage for one scope over one or more days.</summary>
public sealed record VoiceUsageTotals(
    string Scope,
    DateOnly From,
    DateOnly Through,
    IReadOnlyDictionary<VoiceUsageTrackKind, long> SubscriberSeconds,
    IReadOnlyDictionary<VoiceUsageTrackKind, long> TrackSeconds,
    long ParticipantSeconds,
    long Samples)
{
    public long EstimatedEgressBytes => VoiceUsageRates.EgressBytes(SubscriberSeconds);

    public IReadOnlyDictionary<VoiceUsageTrackKind, long> EstimatedEgressBytesByKind =>
        SubscriberSeconds.ToDictionary(pair => pair.Key, pair => VoiceUsageRates.EgressBytes(pair.Key, pair.Value));

    /// <summary>Nothing was recorded for this scope over this window.</summary>
    public bool IsEmpty => Samples == 0 && SubscriberSeconds.Values.All(v => v == 0);

    public static VoiceUsageTotals Empty(string scope, DateOnly from, DateOnly through) =>
        new(scope, from, through,
            new Dictionary<VoiceUsageTrackKind, long>(),
            new Dictionary<VoiceUsageTrackKind, long>(),
            0, 0);
}
