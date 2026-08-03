using Guild.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace Guild.Application.Services;

/// <summary>
/// Resolves the realtime audience for a channel-scoped event: the online guild members who may
/// actually see that channel.
///
/// Guild's realtime handlers used to fan out to <c>GetGuildPresenceAsync(guildId)</c> - every online
/// member of the guild - for events that belong to a single channel. Message content included. The
/// REST path enforces ViewChannel before returning the same content, so the realtime path was
/// silently bypassing the control the REST path implements: any member of the guild who opened the
/// hub received every message posted in every private channel of that guild, while a GET on those
/// channels correctly returned 403.
///
/// <para><b>Cost.</b> This sits on the per-message hot path, so the ViewChannel decision is memoized
/// per (channel, user) for <see cref="DecisionTtl"/>. Steady-state fan-out into a busy channel
/// therefore costs dictionary lookups rather than I/O; only a user who has just come online, or
/// whose entry has aged out, costs a resolution.</para>
///
/// <para><b>Why in-process and not the shared cache.</b> Guild runs as several instances, and the
/// usual reason to prefer a distributed cache is that a per-instance one multiplies the backing
/// work by the instance count. That argument does not apply here, because what this memo replaces
/// is <em>itself</em> a Redis read: resolving a decision reads the per-user permission set Guild
/// already caches in Redis (and deserializes it). Backing the memo with Redis would therefore add a
/// round-trip to avoid a round-trip - strictly worse. Each instance keeping its own memo costs at
/// most one resolution per (channel, user) per instance per TTL, and every instance derives its
/// answer from the same shared permission set, so instances cannot disagree about anything except
/// how recently they looked.
/// (Contrast Bots' BotChannelVisibility, which memoizes a <em>bus</em> round-trip and is therefore
/// correctly backed by the distributed cache.)</para>
///
/// <para><b>Staleness.</b> The TTL bounds how long a revoked permission keeps receiving realtime
/// events after <c>InvalidateUserPermissionsCacheAsync</c> clears the shared entry - the REST path
/// is unaffected and takes effect immediately. It is deliberately short, and far inside the 15
/// minutes the underlying permission cache already allows.</para>
/// </summary>
public class ChannelAudienceService(GuildPermissionService permissions, IMemoryCache cache)
{
    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Filters <paramref name="userIds"/> down to those holding ViewChannel on the channel.
    /// Order is not preserved and duplicates are collapsed.
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
