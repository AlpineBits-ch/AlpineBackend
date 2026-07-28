using Microsoft.Extensions.Caching.Distributed;

namespace Import.Application.Redis;

/// <summary>One-time OAuth "state" correlating a Discord bot-add redirect with the Echo user who
/// requested it - mirrors Identity's steam_state:{stateId} pattern (Identity.Application/
/// Controllers/SteamAuthenticationController.cs) and Bots.Application's PendingInteractionStore.
/// Consumed exactly once on callback.</summary>
public class DiscordImportStateStore(IDistributedCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static string CacheKey(string stateId) => $"import-state:{stateId}";

    public Task SaveAsync(string stateId, string requestingUserId) =>
        cache.SetStringAsync(CacheKey(stateId), requestingUserId,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

    public async Task<string?> ConsumeAsync(string stateId)
    {
        var key = CacheKey(stateId);
        var userId = await cache.GetStringAsync(key);
        if (userId is not null)
        {
            await cache.RemoveAsync(key);
        }
        return userId;
    }
}
