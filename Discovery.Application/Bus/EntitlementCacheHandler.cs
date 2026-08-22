using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Discovery.Api.Bus;

/// <summary>
/// Drops this service's cached entitlements for the subject a <c>billing.EntitlementsChanged</c>
/// names, so a plan change reaches the listing plan gate without waiting out the TTL.
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
