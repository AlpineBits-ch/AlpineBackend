using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// Messaging's read-through view of Identity's <c>UserPrivacySettings</c>.
///
/// <para><b>Redis, not in-memory.</b> These are consulted on every DM send, every conversation
/// creation and every push fan-out, and a stale per-pod copy means a user's "nobody may DM me"
/// toggle silently does not apply on whichever pod happens to serve the next request. The key is
/// <c>privacy_settings:user_id:{id}</c> - the shape §1.4 of docs/specs/privacy.md names, so the
/// eviction handler in every consuming service can agree on it without a shared constant.</para>
///
/// <para><b>Fail closed, but not fail-hard.</b> A read that cannot reach Identity falls back, in
/// order, to (1) the last value this cache saw, even past its refresh window, and only then to
/// (2) <see cref="RestrictiveDefaults"/>. That ordering matters: a value the user actually chose
/// is always a better answer than a guess, and turning a thirty-second Identity blip into
/// "nobody in the system may be messaged" would be an outage dressed up as a privacy control. What
/// is never done is defaulting <i>open</i> - with no data at all the answer is
/// <see cref="DirectMessagePolicy.Friends"/>, never <c>Everyone</c>.</para>
/// </summary>
public sealed class PrivacySettingsCache(
    IDistributedCache cache,
    IMessageBus bus,
    ILogger<PrivacySettingsCache> logger)
{
    /// <summary>The key shape from §1.4 of the privacy spec. Shared verbatim with every other
    /// service's cache so one eviction event can be reasoned about across all of them.</summary>
    public static string KeyFor(string userId) => $"privacy_settings:user_id:{userId}";

    /// <summary>How long a cached record is served without re-asking Identity. Short, because the
    /// eviction event is best-effort: if a <c>UserPrivacySettingsChangedEvent</c> is dropped this
    /// is the upper bound on how long the old answer survives.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long the entry physically survives in Redis. Much longer than
    /// <see cref="RefreshAfter"/> on purpose - past the refresh window the value is still the best
    /// fallback available when Identity cannot be reached.</summary>
    public static readonly TimeSpan HardExpiry = TimeSpan.FromHours(24);

    /// <summary>Identity batches; so does this. Kept well under any realistic bus payload limit.</summary>
    private const int MaxIdsPerRequest = 200;

    /// <summary>
    /// What an account is assumed to have chosen when nothing about it can be discovered.
    ///
    /// <para>Every field is set to the answer that grants the least: DMs from friends only,
    /// discoverability off, profile fields hidden, no read receipts or typing indicators emitted,
    /// every attachment filtered, push content hidden.</para>
    ///
    /// <para><see cref="UserPrivacySettingsSummary.DmRetentionDays"/> is the one deliberate
    /// exception: it stays <c>null</c> ("keep forever"). Retention is the only setting here whose
    /// enforcement <i>destroys</i> data, so guessing at it is not a conservative choice in any
    /// direction that matters - a sweep that ran against a fabricated 30-day window during an
    /// Identity outage would delete history no one asked it to.</para>
    /// </summary>
    public static UserPrivacySettingsSummary RestrictiveDefaults(string userId) => new()
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
        Version = 0,
    };

    private sealed record Envelope(UserPrivacySettingsSummary Value, DateTimeOffset FetchedAt);

    public async Task<UserPrivacySettingsSummary> GetAsync(string userId, CancellationToken ct = default)
    {
        var all = await GetAsync([userId], ct);
        return all.TryGetValue(userId, out var settings) ? settings : RestrictiveDefaults(userId);
    }

    /// <summary>
    /// One entry per requested id, always - a caller may never have to distinguish "absent" from
    /// "unrestricted", which is exactly the mistake that makes a fail-open bug look like a missing
    /// row.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UserPrivacySettingsSummary>> GetAsync(
        IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, UserPrivacySettingsSummary>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;

        var now = DateTimeOffset.UtcNow;
        var stale = new Dictionary<string, UserPrivacySettingsSummary>(StringComparer.Ordinal);
        var needed = new List<string>();

        foreach (var id in ids)
        {
            var envelope = await ReadAsync(id, ct);
            if (envelope is null)
            {
                needed.Add(id);
                continue;
            }

            if (now - envelope.FetchedAt < RefreshAfter)
            {
                result[id] = envelope.Value;
                continue;
            }

            // Past its refresh window: re-ask, but keep the old answer as the fallback rather than
            // dropping to defaults if the re-ask fails.
            stale[id] = envelope.Value;
            needed.Add(id);
        }

        foreach (var chunk in needed.Chunk(MaxIdsPerRequest))
        {
            GetUserPrivacySettingsResponse? response = null;
            try
            {
                response = await bus.InvokeAsync<GetUserPrivacySettingsResponse>(
                    new GetUserPrivacySettingsRequest { UserIds = chunk.ToList() }, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "Privacy settings lookup failed for {Count} users; falling back to the last cached values and then to restrictive defaults",
                    chunk.Length);
            }

            var returned = response?.Settings ?? [];
            foreach (var settings in returned)
            {
                if (string.IsNullOrWhiteSpace(settings.UserId)) continue;
                result[settings.UserId] = settings;
                await WriteAsync(settings, ct);
            }

            foreach (var id in chunk)
            {
                if (result.ContainsKey(id)) continue;

                // Not in the answer, or there was no answer at all.
                result[id] = stale.TryGetValue(id, out var last) ? last : RestrictiveDefaults(id);
            }
        }

        return result;
    }

    /// <summary>Drops one account's cached record. Called from the
    /// <c>UserPrivacySettingsChangedEvent</c> handler - the event carries no values by design, so
    /// the only correct reaction is to forget and re-ask.</summary>
    public async Task InvalidateAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            await cache.RemoveAsync(KeyFor(userId), ct);
        }
        catch (Exception e)
        {
            // A failed eviction means the old value survives until RefreshAfter elapses. Worth a
            // warning, not worth failing the handler and retrying the whole message.
            logger.LogWarning(e, "Failed to evict cached privacy settings for {UserId}", userId);
        }
    }

    private async Task<Envelope?> ReadAsync(string userId, CancellationToken ct)
    {
        try
        {
            var raw = await cache.GetStringAsync(KeyFor(userId), ct);
            if (string.IsNullOrEmpty(raw)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(raw);
            return envelope?.Value is null ? null : envelope;
        }
        catch (Exception e)
        {
            // Redis down, or a value written by an older serialization shape. Both are a miss.
            logger.LogDebug(e, "Cached privacy settings for {UserId} were unreadable", userId);
            return null;
        }
    }

    private async Task WriteAsync(UserPrivacySettingsSummary settings, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync(
                KeyFor(settings.UserId),
                JsonSerializer.Serialize(new Envelope(settings, DateTimeOffset.UtcNow)),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = HardExpiry },
                ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to cache privacy settings for {UserId}", settings.UserId);
        }
    }
}
