using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Social.Api.Services;

/// <summary>Social's read side for the privacy record Identity owns (privacy spec §1.4).</summary>
public class PrivacySettingsCache(IDistributedCache cache, IMessageBus bus, ILogger<PrivacySettingsCache> logger)
{
    public const string KeyPrefix = "privacy_settings:user_id:";

    /// <summary>Short by design: this is a cache in front of an authoritative record that also
    /// evicts on <c>UserPrivacySettingsChangedEvent</c>, so the TTL only bounds how long a *missed*
    /// eviction can survive.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static string KeyFor(string userId) => $"{KeyPrefix}{userId}";

    /// <summary>What a caller gets when the record cannot be resolved.</summary>
    public static UserPrivacySettingsSummary RestrictiveDefaults(string userId) => new()
    {
        UserId = userId,
        AllowDataCollection = false,
        AllowPersonalization = false,
        AllowVoiceRecordingInClips = false,
        // Friends, not Nobody: the spec names Friends as the restrictive DM default, because
        // Nobody would break DMs between established friends on every transient bus failure.
        DirectMessagePolicy = DirectMessagePolicy.Friends,
        FriendRequestPolicy = FriendRequestPolicy.Nobody,
        DiscoverableByUsername = false,
        DiscoverableByEmail = false,
        DiscoverableByPhone = false,
        MutualServersVisibility = Visibility.Nobody,
        MutualFriendsVisibility = Visibility.Nobody,
        ConnectionsVisibility = Visibility.Nobody,
        BirthdayVisibility = Visibility.Nobody,
        ShareActivity = false,
        AllowPositionalVoiceCapture = false,
        SendReadReceipts = false,
        SendTypingIndicators = false,
        DmRetentionDays = null,
        ExplicitContentFilter = ExplicitContentFilter.Everyone,
        HidePushContent = true,
        Version = 0,
    };

    public async Task<UserPrivacySettingsSummary> GetAsync(string userId, CancellationToken token = default)
    {
        var all = await GetManyAsync([userId], token);
        return all.TryGetValue(userId, out var settings) ? settings : RestrictiveDefaults(userId);
    }

    /// <summary>Resolves a whole set at once.</summary>
    public async Task<IReadOnlyDictionary<string, UserPrivacySettingsSummary>> GetManyAsync(
        IEnumerable<string> userIds, CancellationToken token = default)
    {
        var wanted = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var resolved = new Dictionary<string, UserPrivacySettingsSummary>();
        if (wanted.Count == 0) return resolved;

        var missing = new List<string>();

        foreach (var userId in wanted)
        {
            var cached = await TryReadAsync(userId, token);
            if (cached is not null) resolved[userId] = cached;
            else missing.Add(userId);
        }

        if (missing.Count == 0) return resolved;

        GetUserPrivacySettingsResponse? response = null;
        try
        {
            response = await bus.InvokeAsync<GetUserPrivacySettingsResponse>(
                new GetUserPrivacySettingsRequest { UserIds = missing }, token);
        }
        catch (Exception e)
        {
            // Deliberately swallowed: the fallback below is the fail-closed behaviour the spec
            // demands, and rethrowing would turn a privacy lookup outage into a 500 on every
            // profile read.
            logger.LogWarning(e, "Privacy settings lookup failed for {Count} user(s); applying restrictive defaults", missing.Count);
        }

        foreach (var settings in response?.Settings ?? [])
        {
            if (string.IsNullOrWhiteSpace(settings.UserId)) continue;
            resolved[settings.UserId] = settings;
            await TryWriteAsync(settings, token);
        }

        // Anything Identity did not answer for - a failed call, or an id it has no row for - gets
        // the restrictive defaults, and is NOT written to the cache: a fallback must never be
        // mistaken for a real read on the next request.
        foreach (var userId in missing.Where(id => !resolved.ContainsKey(id)))
            resolved[userId] = RestrictiveDefaults(userId);

        return resolved;
    }

    /// <summary>Drops one user's key.</summary>
    public async Task EvictAsync(string userId, CancellationToken token = default)
    {
        try
        {
            await cache.RemoveAsync(KeyFor(userId), token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to evict cached privacy settings for {UserId}", userId);
        }
    }

    private async Task<UserPrivacySettingsSummary?> TryReadAsync(string userId, CancellationToken token)
    {
        try
        {
            var bytes = await cache.GetAsync(KeyFor(userId), token);
            if (bytes is null || bytes.Length == 0) return null;
            return JsonSerializer.Deserialize<UserPrivacySettingsSummary>(bytes);
        }
        catch (Exception e)
        {
            // A poisoned or unreachable cache entry is a miss, not a failure - the bus call below
            // is the authoritative path anyway.
            logger.LogWarning(e, "Failed to read cached privacy settings for {UserId}", userId);
            return null;
        }
    }

    private async Task TryWriteAsync(UserPrivacySettingsSummary settings, CancellationToken token)
    {
        try
        {
            await cache.SetAsync(KeyFor(settings.UserId), JsonSerializer.SerializeToUtf8Bytes(settings),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to cache privacy settings for {UserId}", settings.UserId);
        }
    }
}
