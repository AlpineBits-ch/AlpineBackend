using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Bus;

/// <summary>
/// Drops this gateway's cached entitlements for the subject a <c>billing.EntitlementsChanged</c>
/// names.
/// </summary>
public class EntitlementCacheHandler
{
    public static Task Handle(
        EntitlementsChanged message,
        EntitlementCacheInvalidator cache,
        IPlanCatalogueSource catalogue)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(catalogue);

        // The one reason that moved the plan table rather than one subject's grants.
        if (message.Reason == EntitlementsChangedReason.PlanVersionActivated) catalogue.Invalidate();

        return cache.InvalidateAsync(new EntitlementSubject(message.SubjectKind, message.SubjectId));
    }
}
