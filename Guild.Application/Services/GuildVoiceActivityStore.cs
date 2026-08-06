using Echo.Realtime.Caching;
using Guild.Application.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Services;

/// <summary>
/// Maintains and reads <see cref="GuildVoiceActivity"/> - the per-guild index of who is in which
/// voice channel.
/// </summary>
public class GuildVoiceActivityStore(IDistributedLockService locks, IDistributedCache cache)
{
    /// <summary>Matches the channel-blob expiry.</summary>
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    public Task AddParticipantAsync(string guildId, string channelId, string userId, CancellationToken ct = default) =>
        MutateAsync(guildId, activity =>
        {
            var channel = GetOrAdd(activity, channelId);
            if (!channel.UserIds.Contains(userId)) channel.UserIds.Add(userId);
        }, ct);

    public Task RemoveParticipantAsync(string guildId, string channelId, string userId, CancellationToken ct = default) =>
        MutateAsync(guildId, activity =>
        {
            if (!activity.Channels.TryGetValue(channelId, out var channel)) return;
            channel.UserIds.Remove(userId);
            channel.StreamerIds.Remove(userId);
        }, ct);

    /// <summary>Moves a participant between two channels of the same guild under a single lock -
    /// a move is one fact, and briefly indexing someone in both channels or neither is a fact
    /// nobody should be able to read.</summary>
    public Task MoveParticipantAsync(string guildId, string fromChannelId, string toChannelId, string userId,
        CancellationToken ct = default) =>
        MutateAsync(guildId, activity =>
        {
            if (activity.Channels.TryGetValue(fromChannelId, out var from))
            {
                from.UserIds.Remove(userId);
                from.StreamerIds.Remove(userId);
            }

            var to = GetOrAdd(activity, toChannelId);
            if (!to.UserIds.Contains(userId)) to.UserIds.Add(userId);
        }, ct);

    public Task SetStreamingAsync(string guildId, string channelId, string userId, bool isStreaming,
        CancellationToken ct = default) =>
        MutateAsync(guildId, activity =>
        {
            if (!activity.Channels.TryGetValue(channelId, out var channel)) return;
            if (isStreaming)
            {
                if (!channel.StreamerIds.Contains(userId)) channel.StreamerIds.Add(userId);
            }
            else
            {
                channel.StreamerIds.Remove(userId);
            }
        }, ct);

    /// <summary>
    /// Replaces one guild's whole index with state recomputed from the channel blobs.
    /// </summary>
    public Task ReplaceAsync(string guildId, Dictionary<string, ChannelVoiceActivity> channels,
        CancellationToken ct = default) =>
        MutateAsync(guildId, activity =>
        {
            activity.Channels.Clear();
            foreach (var (channelId, channel) in channels) activity.Channels[channelId] = channel;
        }, ct);

    public async Task<GuildVoiceActivity?> LoadAsync(string guildId, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(GuildVoiceActivity.GetCacheKey(guildId), ct);
        return raw is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<GuildVoiceActivity>(raw);
    }

    private static ChannelVoiceActivity GetOrAdd(GuildVoiceActivity activity, string channelId)
    {
        if (activity.Channels.TryGetValue(channelId, out var existing)) return existing;
        var created = new ChannelVoiceActivity();
        activity.Channels[channelId] = created;
        return created;
    }

    private async Task MutateAsync(string guildId, Action<GuildVoiceActivity> mutate, CancellationToken ct)
    {
        var key = GuildVoiceActivity.GetCacheKey(guildId);
        await using var _ = await locks.AcquireAsync(key, ct: ct);

        var raw = await cache.GetStringAsync(key, ct);
        var activity = (raw is null
                        ? null
                        : System.Text.Json.JsonSerializer.Deserialize<GuildVoiceActivity>(raw))
                       ?? new GuildVoiceActivity { GuildId = guildId };
        activity.GuildId = guildId;

        mutate(activity);

        foreach (var channelId in activity.Channels
                     .Where(c => c.Value.UserIds.Count == 0)
                     .Select(c => c.Key)
                     .ToList())
        {
            activity.Channels.Remove(channelId);
        }

        await cache.SetStringAsync(key, System.Text.Json.JsonSerializer.Serialize(activity), CacheOptions, ct);
    }
}
