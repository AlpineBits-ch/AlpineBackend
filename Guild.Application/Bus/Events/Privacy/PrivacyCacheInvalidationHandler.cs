using Guild.Application.Services;
using Identity.Contracts.Bus.Events;
using Social.Contracts.Bus.Integration.Events;
using Wolverine.Attributes;

namespace Guild.Application.Bus.Events.Privacy;

/// <summary>
/// Keeps Guild's two policy caches honest.
///
/// <para>Both caches carry a TTL as a backstop, but a TTL is the wrong mechanism for the thing that
/// actually matters here: someone who turns off push content, or presses block, did so because of
/// something happening now. Waiting out an expiry means the control demonstrably does not work for
/// the five minutes it is needed most. These events exist so the eviction is immediate; the TTL
/// only covers a dropped one.</para>
///
/// <para>Non-transactional - eviction touches Redis, never the database, so enrolling it in the EF
/// transaction middleware would open a connection for nothing.</para>
/// </summary>
[NonTransactional]
public class PrivacyCacheInvalidationHandler
{
    public static Task Handle(UserPrivacySettingsChangedEvent message, PrivacySettingsCache cache) =>
        cache.InvalidateAsync(message.UserId);

    /// <summary>Both sides. The state stored under one key lists blocks in both directions, so a
    /// new block invalidates the blocker's entry and the blocked user's alike.</summary>
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
