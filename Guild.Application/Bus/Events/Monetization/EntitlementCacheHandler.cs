using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;

namespace Guild.Application.Bus.Events.Monetization;

/// <summary>
/// Drops this service's cached entitlements for the subject a <c>billing.EntitlementsChanged</c>
/// names, so a plan change reaches the voice and feature ceilings without waiting out the TTL.
/// </summary>
public class EntitlementCacheHandler
{
    public static Task Handle(EntitlementsChanged message, EntitlementCacheInvalidator cache)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(cache);

        return cache.InvalidateAsync(new EntitlementSubject(message.SubjectKind, message.SubjectId));
    }
}
