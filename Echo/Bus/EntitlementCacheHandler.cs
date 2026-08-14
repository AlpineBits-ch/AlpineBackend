using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;

namespace Echo.Bus;

/// <summary>
/// Drops this gateway's cached entitlements for the subject a <c>billing.EntitlementsChanged</c>
/// names.
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
