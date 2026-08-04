using Guild.Application.Services;
using Identity.Contracts.Bus.Events;
using Social.Contracts.Bus.Integration.Events;
using Wolverine.Attributes;

namespace Guild.Application.Bus.Events.Privacy;

/// <summary>Keeps Guild's two policy caches honest.</summary>
[NonTransactional]
public class PrivacyCacheInvalidationHandler
{
    public static Task Handle(UserPrivacySettingsChangedEvent message, PrivacySettingsCache cache) =>
        cache.InvalidateAsync(message.UserId);

    /// <summary>Both sides.</summary>
    public static async Task Handle(UserBlockedEvent message, BlockCache cache)
    {
        await cache.InvalidateAsync(message.BlockerId);
        await cache.InvalidateAsync(message.BlockedId);
    }

    public static async Task Handle(UserUnblockedEvent message, BlockCache cache)
    {
        await cache.InvalidateAsync(message.BlockerId);
        await cache.InvalidateAsync(message.BlockedId);
    }
}
