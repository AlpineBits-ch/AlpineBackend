using System.Security.Cryptography;
using System.Text;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Bots.Application.Gateway;

public interface IBotChannelVisibility
{
    /// <summary>
    /// Returns the subset of <paramref name="botUserIds"/> holding ViewChannel on the channel.
    /// </summary>
    Task<IReadOnlyList<string>> FilterToVisibleAsync(string channelId, List<string> botUserIds);
}

/// <summary>
/// Narrows "bots installed in this guild" down to "bots that may actually see this channel".
/// </summary>
public class BotChannelVisibility(IMessageBus bus, IDistributedCache cache, ILogger<BotChannelVisibility> logger)
    : IBotChannelVisibility
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private const char Separator = '\n';

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterToVisibleAsync(string channelId, List<string> botUserIds)
    {
        if (botUserIds.Count == 0) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(channelId)) return Array.Empty<string>();

        var cacheKey = BuildCacheKey(channelId, botUserIds);

        try
        {
            var cached = await cache.GetStringAsync(cacheKey);
            if (cached is not null)
            {
                return cached.Length == 0
                    ? Array.Empty<string>()
                    : cached.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            }
        }
        catch (Exception ex)
        {
            // A cache outage must not take gateway dispatch down with it - fall through and ask
            // Guild directly.
            logger.LogWarning(ex, "Bot channel visibility cache read failed for channel {ChannelId}.", channelId);
        }

        try
        {
            var response = await bus.InvokeAsync<FilterUsersWithChannelPermissionResponse>(
                new FilterUsersWithChannelPermissionRequest
                {
                    ChannelId = channelId,
                    UserIds = botUserIds,
                    Permission = ExternalPermission.ViewChannel,
                });

            var allowed = response.AllowedUserIds.ToArray();

            await cache.SetStringAsync(cacheKey, string.Join(Separator, allowed), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheTtl,
            });

            return allowed;
        }
        catch (Exception ex)
        {
            // Deliberately not cached - a transient Guild failure must not pin "deny everything"
            // for the whole TTL. This dispatch is dropped rather than sent unfiltered.
            logger.LogError(ex,
                "Could not resolve bot channel visibility for channel {ChannelId}; skipping gateway dispatch.",
                channelId);
            return Array.Empty<string>();
        }
    }

    private static string BuildCacheKey(string channelId, List<string> botUserIds)
    {
        // Order-independent so an incidental reordering from the DB doesn't miss the cache.
        var ordered = botUserIds.OrderBy(id => id, StringComparer.Ordinal);
        var joined = string.Join(Separator, ordered);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(joined));

        return $"botvis:{channelId}:{Convert.ToHexStringLower(digest)[..16]}";
    }
}
