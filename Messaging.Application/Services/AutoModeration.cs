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

    private static async Task<GetGuildAutoModConfigResponse?> GetConfigAsync(string channelId, IDistributedCache cache, IMessageBus bus)
    {
        var cacheKey = ConfigCacheKey(channelId);
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached is not null) return JsonSerializer.Deserialize<GetGuildAutoModConfigResponse>(cached);

        var response = await bus.InvokeAsync<GetGuildAutoModConfigResponse>(new GetGuildAutoModConfigRequest { ChannelId = channelId });

        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
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
        var raw = await cache.GetStringAsync(key);
        var count = (raw is null ? 0 : int.Parse(raw)) + 1;

        // Fixed window, reset on every message in the burst - simple to reason about, and the
        // practical difference from a true sliding window doesn't matter at spam-prevention scale.
        await cache.SetStringAsync(key, count.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(intervalSeconds),
        });

        return count > maxMessages;
    }
}
