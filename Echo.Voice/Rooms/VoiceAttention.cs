using Echo.Realtime.Caching;
using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Voice.Rooms;

/// <summary>
/// What one participant is doing as a publisher: the speech signal the ranking is built from.
/// </summary>
public sealed class VoiceSpeakerState
{
    public bool IsSpeaking { get; set; }

    /// <summary>
    /// Unix milliseconds at which the current run of speech began, or 0 when not speaking.
    /// </summary>
    public long SpeakingSinceUnixMs { get; set; }

    /// <summary>
    /// Unix milliseconds of the last run of speech that lasted long enough to count.
    /// </summary>
    public long LastSpokeAtUnixMs { get; set; }
}

/// <summary>
/// What one participant is doing as a subscriber: everything they have told the server about what
/// they can actually see and want to hear.
/// </summary>
public sealed class VoiceSubscriberState
{
    /// <summary>The client is backgrounded or hidden.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Publishers this subscriber always wants, ranking notwithstanding.</summary>
    public List<string> Pinned { get; set; } = [];

    /// <summary>Publishers whose tile this subscriber has collapsed.</summary>
    public List<string> PausedPublishers { get; set; } = [];

    /// <summary>Publisher user id to the height in device pixels of the largest tile this
    /// subscriber renders them in. Drives simulcast layer selection.</summary>
    public Dictionary<string, int> TileHeights { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Share ids whose audio half this subscriber has asked for.</summary>
    public List<string> ScreenAudioShares { get; set; } = [];
}

/// <summary>One member of the ranked set, with the moment they entered it.</summary>
public sealed class VoiceActiveSpeaker
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>Unix milliseconds.</summary>
    public long EnteredAtUnixMs { get; set; }
}

/// <summary>The whole attention table for one room.</summary>
public sealed class VoiceAttention
{
    public Dictionary<string, VoiceSpeakerState> Speakers { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, VoiceSubscriberState> Subscribers { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>The current ranked set, in rank order.</summary>
    public List<VoiceActiveSpeaker> ActiveSpeakers { get; set; } = [];

    /// <summary>Unix milliseconds of the last selection run, so a heartbeat can tell a stale set
    /// from a settled one without storing a timer.</summary>
    public long SelectedAtUnixMs { get; set; }

    /// <summary>Bumped whenever the selected set changes.</summary>
    public long Revision { get; set; }

    public VoiceSpeakerState Speaker(string userId)
    {
        if (Speakers.TryGetValue(userId, out var state)) return state;
        return Speakers[userId] = new VoiceSpeakerState();
    }

    public VoiceSubscriberState Subscriber(string userId)
    {
        if (Subscribers.TryGetValue(userId, out var state)) return state;
        return Subscribers[userId] = new VoiceSubscriberState();
    }

    /// <summary>Drops everything about people who are no longer in the room, in both directions.
    /// Without it a long-lived channel accumulates the attention state of everybody who was ever in
    /// it, and a departed participant could keep a ranked slot nobody can hear.</summary>
    public bool PruneTo(IReadOnlyCollection<string> presentUserIds)
    {
        var present = presentUserIds as HashSet<string> ?? presentUserIds.ToHashSet(StringComparer.Ordinal);

        var changed = false;
        foreach (var userId in Speakers.Keys.Where(k => !present.Contains(k)).ToList())
        {
            Speakers.Remove(userId);
            changed = true;
        }

        foreach (var userId in Subscribers.Keys.Where(k => !present.Contains(k)).ToList())
        {
            Subscribers.Remove(userId);
            changed = true;
        }

        foreach (var state in Subscribers.Values)
        {
            changed |= state.Pinned.RemoveAll(id => !present.Contains(id)) > 0;
            changed |= state.PausedPublishers.RemoveAll(id => !present.Contains(id)) > 0;
            foreach (var userId in state.TileHeights.Keys.Where(k => !present.Contains(k)).ToList())
            {
                state.TileHeights.Remove(userId);
                changed = true;
            }
        }

        changed |= ActiveSpeakers.RemoveAll(s => !present.Contains(s.UserId)) > 0;
        return changed;
    }
}

/// <summary>Reads and writes <see cref="VoiceAttention"/>, and nothing else does.</summary>
public sealed class VoiceAttentionStore(
    IDistributedLockService locks,
    IDistributedCache cache,
    VoiceSubscriptionOptions options)
{
    private readonly DistributedCacheEntryOptions _cacheOptions = new()
    {
        SlidingExpiration = options.AttentionTtl,
    };

    public static string CacheKey(VoiceRoomKey key) => $"voice:attention:{key.Kind}:{key.Id}";

    /// <summary>Unlocked read.</summary>
    public async Task<VoiceAttention> LoadAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(CacheKey(key), ct);
        if (raw is null) return new VoiceAttention();
        return System.Text.Json.JsonSerializer.Deserialize<VoiceAttention>(raw) ?? new VoiceAttention();
    }

    /// <summary>Load-or-create, mutate, save, under the attention key's lock.</summary>
    public async Task<VoiceAttention> MutateAsync(
        VoiceRoomKey key, Func<VoiceAttention, bool> mutate, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(key);
        await using var _ = await locks.AcquireAsync(cacheKey, ct: ct);

        var raw = await cache.GetStringAsync(cacheKey, ct);
        var attention = raw is null
            ? new VoiceAttention()
            : System.Text.Json.JsonSerializer.Deserialize<VoiceAttention>(raw) ?? new VoiceAttention();

        if (!mutate(attention)) return attention;

        await cache.SetStringAsync(
            cacheKey, System.Text.Json.JsonSerializer.Serialize(attention), _cacheOptions, ct);
        return attention;
    }

    /// <summary>Forgets a room's attention entirely - it was reaped, so nothing in here can still
    /// be true.</summary>
    public Task DropAsync(VoiceRoomKey key, CancellationToken ct = default) =>
        cache.RemoveAsync(CacheKey(key), ct);
}
