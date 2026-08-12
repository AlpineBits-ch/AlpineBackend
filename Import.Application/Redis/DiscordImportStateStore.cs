using Microsoft.Extensions.Caching.Distributed;

namespace Import.Application.Redis;

/// <summary>What the state token was created for: who asked, and where to put them afterwards.</summary>
/// <param name="RequestingUserId">The Echo account that started the import.</param>
/// <param name="ReturnUrl">Where the callback redirects when it is done. Already resolved against
/// <c>DiscordImportReturnTargets.Allowed</c> when it was stored - the callback does not re-validate,
/// because nothing an untrusted caller sends ever reaches this field.</param>
public record DiscordImportState(string RequestingUserId, string ReturnUrl);

/// <summary>
/// One-time OAuth "state" correlating a Discord bot-add redirect with the Echo user who requested
/// it - mirrors Identity's steam_state:{stateId} pattern (Identity.Application/
/// Controllers/SteamAuthenticationController.cs) and Bots.Application's PendingInteractionStore.
/// </summary>
public class DiscordImportStateStore(IDistributedCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static string CacheKey(string stateId) => $"import-state:{stateId}";

    public Task SaveAsync(string stateId, string requestingUserId, string returnUrl) =>
        cache.SetStringAsync(CacheKey(stateId), $"{requestingUserId}\n{returnUrl}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

    public async Task<DiscordImportState?> ConsumeAsync(string stateId)
    {
        var key = CacheKey(stateId);
        var stored = await cache.GetStringAsync(key);
        if (stored is null) return null;

        await cache.RemoveAsync(key);

        var split = stored.IndexOf('\n');
        return split < 0
            ? new DiscordImportState(stored, Endpoints.DiscordImportReturnTargets.Default)
            : new DiscordImportState(stored[..split], stored[(split + 1)..]);
    }
}
