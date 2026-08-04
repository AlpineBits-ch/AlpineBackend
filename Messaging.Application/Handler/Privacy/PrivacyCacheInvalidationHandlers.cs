using Identity.Contracts.Bus.Events;
using Messaging.Application.Services.Privacy;
using Social.Contracts.Bus.Integration.Events;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Privacy;

/// <summary>
/// Keeps Messaging's two policy caches honest.
///
/// <para>Neither event carries values - both are eviction signals, so the only correct reaction is
/// to forget and re-ask on the next read. Marked <see cref="NonTransactionalAttribute"/> because
/// nothing here touches the database and the transactional middleware would open a connection per
/// event for no reason.</para>
///
/// <para>The block events matter more than the settings one: a user who presses block is trying to
/// stop something that is happening right now, and a block that takes effect at the next cache
/// expiry has already failed them once.</para>
/// </summary>
[NonTransactional]
public class PrivacyCacheInvalidationHandlers
{
    public static Task Handle(UserPrivacySettingsChangedEvent evt, PrivacySettingsCache cache) =>
        cache.InvalidateAsync(evt.UserId);

    /// <summary>Both sides are evicted. The record is keyed per user and holds the relation in both
    /// directions, so a block only half-evicted is a block the blocked party's pod still cannot
    /// see.</summary>
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
