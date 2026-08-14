namespace Echo.Voice.Usage;

/// <summary>
/// The handful of Redis operations <see cref="VoiceUsageMeter"/> needs, and nothing else.
/// </summary>
public interface IVoiceUsageBackend
{
    /// <summary>
    /// Sets <paramref name="key"/> only if it does not already exist, returning whether this caller
    /// was the one that set it.
    /// </summary>
    Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct);

    Task<long?> ReadCounterAsync(string key, CancellationToken ct);

    Task WriteCounterAsync(string key, long value, TimeSpan ttl, CancellationToken ct);

    /// <summary>Adds each delta to the matching hash field, creating the hash if needed, and
    /// refreshes the hash's expiry.</summary>
    Task AccumulateAsync(
        string hashKey, IReadOnlyList<KeyValuePair<string, long>> deltas, TimeSpan ttl,
        CancellationToken ct);

    Task AddToSetAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct);

    Task<IReadOnlyDictionary<string, long>> ReadHashAsync(string hashKey, CancellationToken ct);

    Task<IReadOnlyList<string>> ReadSetAsync(string setKey, CancellationToken ct);
}
