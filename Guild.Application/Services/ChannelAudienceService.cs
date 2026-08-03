using Guild.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace Guild.Application.Services;

/// <summary>
/// Resolves the realtime audience for a channel-scoped event: the online guild members who may
/// actually see that channel.
/// </summary>
public class ChannelAudienceService(GuildPermissionService permissions, IMemoryCache cache)
{
    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Filters <paramref name="userIds"/> down to those holding ViewChannel on the channel.
    /// </summary>
    public async Task<List<string>> FilterToViewersAsync(string channelId, IEnumerable<string> userIds)
    {
        var candidates = userIds.Distinct().ToList();
        var viewers = new List<string>(candidates.Count);

        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(channelId)) return viewers;

        var unresolved = new List<string>();

        foreach (var userId in candidates)
        {
            if (cache.TryGetValue<bool>(DecisionKey(channelId, userId), out var allowed))
            {
                if (allowed) viewers.Add(userId);
            }
            else
            {
                unresolved.Add(userId);
            }
        }

        if (unresolved.Count == 0) return viewers;

        // Batched: resolves the channel and the guild owner once for the whole set rather than
        // per user (see GuildPermissionService.FilterUsersWithChannelPermissionAsync).
        var allowedIds = await permissions.FilterUsersWithChannelPermissionAsync(
            channelId, unresolved, Permissions.ViewChannel);

        var allowedSet = allowedIds.ToHashSet(StringComparer.Ordinal);

        foreach (var userId in unresolved)
        {
            var isViewer = allowedSet.Contains(userId);
            cache.Set(DecisionKey(channelId, userId), isViewer, DecisionTtl);
            if (isViewer) viewers.Add(userId);
        }

        return viewers;
    }

    private static string DecisionKey(string channelId, string userId) => $"chanaud:{channelId}:{userId}";
}
