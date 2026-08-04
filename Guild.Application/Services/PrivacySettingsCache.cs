using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>
/// Guild's read-through view of Identity's <c>UserPrivacySettings</c> (privacy spec §1.4).
/// </summary>
public class PrivacySettingsCache(
    IDistributedCache cache,
    IMessageBus bus,
    ILogger<PrivacySettingsCache> logger)
{
    /// <summary>Long enough that the steady state is a Redis read, short enough that a dropped
    /// invalidation event is a nuisance rather than a lasting wrong answer.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public static string KeyFor(string userId) => $"privacy_settings:user_id:{userId}";

    /// <summary>What an unresolvable user is treated as.</summary>
    public static UserPrivacySettingsSummary Restrictive(string userId) => new()
    {
        UserId = userId,

        AllowDataCollection = false,
        AllowPersonalization = false,
        AllowVoiceRecordingInClips = false,

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

        // Below every real version, so a cached value never loses to this one.
        Version = -1,
    };

    /// <summary>
    /// Resolves many users at once: one Redis read per user, then one bus round trip for whatever
    /// was missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UserPrivacySettingsSummary>> GetAsync(
        IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var resolved = new Dictionary<string, UserPrivacySettingsSummary>(StringComparer.Ordinal);
        if (ids.Count == 0) return resolved;

        var missing = new List<string>();

        foreach (var id in ids)
        {
            var hit = await TryReadAsync(id, ct);
            if (hit is not null) resolved[id] = hit;
            else missing.Add(id);
        }

        if (missing.Count == 0) return resolved;

        try
        {
            var response = await bus.InvokeAsync<GetUserPrivacySettingsResponse>(
                new GetUserPrivacySettingsRequest { UserIds = missing }, ct);

            foreach (var setting in response.Settings)
            {
                if (string.IsNullOrWhiteSpace(setting.UserId)) continue;
                resolved[setting.UserId] = setting;
                await TryWriteAsync(setting, ct);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Could not resolve privacy settings for {Count} user(s) from Identity; applying restrictive defaults",
                missing.Count);
        }

        // Anything Identity did not answer for - because the call failed, or because it simply
        // omitted an id - is restrictive. "No answer" must never read as "no restriction".
        foreach (var id in missing)
        {
            if (!resolved.ContainsKey(id)) resolved[id] = Restrictive(id);
        }

        return resolved;
    }

    /// <summary>One user.</summary>
    public async Task<UserPrivacySettingsSummary> GetAsync(string userId, CancellationToken ct = default)
    {
        var many = await GetAsync([userId], ct);
        return many.TryGetValue(userId, out var settings) ? settings : Restrictive(userId);
    }

    /// <summary>Drops one user's entry.</summary>
    public async Task InvalidateAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            await cache.RemoveAsync(KeyFor(userId), ct);
        }
        catch (Exception e)
        {
            // A failed eviction is bounded by the TTL, and re-throwing would send the invalidation
            // event to the error queue for a condition retrying cannot fix any faster.
            logger.LogWarning(e, "Could not evict cached privacy settings for user {UserId}", userId);
        }
    }

    private async Task<UserPrivacySettingsSummary?> TryReadAsync(string userId, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(KeyFor(userId), ct);
            if (bytes is null || bytes.Length == 0) return null;
            return JsonSerializer.Deserialize<UserPrivacySettingsSummary>(bytes);
        }
        catch (Exception e)
        {
            // A corrupt or unreachable entry is a miss, not a failure - the bus is asked next, and
            // only if that also fails does the restrictive default apply.
            logger.LogWarning(e, "Could not read cached privacy settings for user {UserId}", userId);
            return null;
        }
    }

    private async Task TryWriteAsync(UserPrivacySettingsSummary setting, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(
                KeyFor(setting.UserId),
                JsonSerializer.SerializeToUtf8Bytes(setting),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not cache privacy settings for user {UserId}", setting.UserId);
        }
    }
}
