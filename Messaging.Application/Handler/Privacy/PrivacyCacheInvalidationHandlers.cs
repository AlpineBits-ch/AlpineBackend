using Identity.Contracts.Bus.Events;
using Messaging.Application.Services.Privacy;
using Social.Contracts.Bus.Integration.Events;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Privacy;

/// <summary>Keeps Messaging's two policy caches honest.</summary>
[NonTransactional]
public class PrivacyCacheInvalidationHandlers
{
    public static Task Handle(UserPrivacySettingsChangedEvent evt, PrivacySettingsCache cache) =>
        cache.InvalidateAsync(evt.UserId);

    /// <summary>Both sides are evicted.</summary>
    public static async Task Handle(UserBlockedEvent evt, BlockCache cache)
    {
        await cache.InvalidateAsync(evt.BlockerId);
        await cache.InvalidateAsync(evt.BlockedId);
    }

    public static async Task Handle(UserUnblockedEvent evt, BlockCache cache)
    {
        await cache.InvalidateAsync(evt.BlockerId);
        await cache.InvalidateAsync(evt.BlockedId);
    }
}
