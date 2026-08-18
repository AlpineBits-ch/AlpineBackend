using System.Text.Json;
using System.Text.RegularExpressions;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>Guild-configured message auto-moderation - blocked-word filter plus a simple
/// per-user-per-channel rate limit. Config lives in Guild.Application (guild-owned data);
/// Messaging caches it here to avoid a cross-service round trip on every single message send.</summary>
public static class AutoModeration
{
    private static readonly TimeSpan ConfigCacheLifetime = TimeSpan.FromMinutes(5);

    private static string ConfigCacheKey(string channelId) => $"automod:config:{channelId}";
    private static string RateLimitKey(string channelId, string userId) => $"automod:rate:{channelId}:{userId}";

    /// <summary>Returns a blocked reason ("blocked_word" / "rate_limited") if the message should be
    /// rejected, or null if it's clear to send.</summary>
    public static async Task<string?> CheckAsync(string channelId, string userId, string content, IDistributedCache cache, IMessageBus bus)
    {
        var config = await GetConfigAsync(channelId, cache, bus);
        if (config is null || !config.Enabled) return null;

        if (ContainsBlockedWord(content, config.BlockedWords)) return "blocked_word";

        if (config.MaxMessagesPerInterval is > 0 && config.IntervalSeconds is > 0)
        {
            if (await IsRateLimitedAsync(channelId, userId, config.MaxMessagesPerInterval.Value, config.IntervalSeconds.Value, cache))
                return "rate_limited";
        }

        return null;
    }

    /// <summary>Drops the cached config for one channel, so the next send reads the guild's current
    /// rules instead of the ones that were in force when the cache was filled.</summary>
    public static Task EvictConfigAsync(string channelId, IDistributedCache cache) =>
        cache.RemoveAsync(ConfigCacheKey(channelId));

    private static async Task<GetGuildAutoModConfigResponse?> GetConfigAsync(string channelId, IDistributedCache cache, IMessageBus bus)
    {
        var cacheKey = ConfigCacheKey(channelId);
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached is not null) return JsonSerializer.Deserialize<GetGuildAutoModConfigResponse>(cached);

        var response = await bus.InvokeAsync<GetGuildAutoModConfigResponse>(new GetGuildAutoModConfigRequest { ChannelId = channelId });

        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ConfigCacheLifetime,
        });

        return response;
    }

    /// <summary>Public (rather than private) specifically so it's unit-testable without needing
    /// to fake the whole IMessageBus surface just to exercise the word-matching rules.</summary>
    public static bool ContainsBlockedWord(string content, List<string> blockedWords)
    {
        if (blockedWords.Count == 0 || string.IsNullOrWhiteSpace(content)) return false;

        foreach (var word in blockedWords)
        {
            if (string.IsNullOrWhiteSpace(word)) continue;
            if (Regex.IsMatch(content, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Public for the same testability reason as ContainsBlockedWord - only needs a
    /// fakeable IDistributedCache, not the bus.</summary>
    public static async Task<bool> IsRateLimitedAsync(string channelId, string userId, int maxMessages, int intervalSeconds, IDistributedCache cache)
    {
        var key = RateLimitKey(channelId, userId);
        var now = DateTimeOffset.UtcNow;
        var (startedAt, previousCount) = ReadWindow(await cache.GetStringAsync(key), now, intervalSeconds);
        var count = previousCount + 1;

        // The window is anchored to its first message and the expiry never moves: a rejected send
        // must not push it out, or a client that keeps retrying holds itself blocked indefinitely.
        await cache.SetStringAsync(key, $"{startedAt.ToUnixTimeSeconds()}:{count}", new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = startedAt.AddSeconds(intervalSeconds),
        });

        return count > maxMessages;
    }

    /// <summary>The window the caller's message falls into - the stored one while it is still open,
    /// a fresh one otherwise.</summary>
    private static (DateTimeOffset StartedAt, int Count) ReadWindow(string? raw, DateTimeOffset now, int intervalSeconds)
    {
        var separator = raw?.IndexOf(':') ?? -1;
        if (raw is not null && separator > 0
            && long.TryParse(raw[..separator], out var startedAtUnix)
            && int.TryParse(raw[(separator + 1)..], out var count))
        {
            var startedAt = DateTimeOffset.FromUnixTimeSeconds(startedAtUnix);

            // A start in the future, or one whose interval has already elapsed, belongs to a window
            // this message is not in - most likely because the guild shortened the interval.
            if (startedAt <= now && startedAt.AddSeconds(intervalSeconds) > now) return (startedAt, count);
        }

        return (now, 0);
    }
}
