using Billing.Contracts.Bus.Events;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Bus;

/// <summary>
/// A lapsed <c>guild.public_listing</c> entitlement suspends the guild's published listing. It never
/// deletes it, and regaining the flag never republishes - that is the owner's own action.
/// </summary>
public class EntitlementsChangedHandler
{
    // No SaveChangesAsync here: Wolverine's AutoApplyTransactions policy commits on a successful
    // return.
    public static async Task Handle(
        EntitlementsChanged message,
        MicroserviceContext ctx,
        ListingRealtime realtime,
        EntitlementResolver entitlements,
        EntitlementCacheInvalidator cache,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.SubjectKind != SubjectKind.Guild) return;

        // ChangedKeys is advisory and may be empty or omit a real change (EntitlementsChanged.cs) -
        // only skip when it is populated and names other keys.
        if (message.ChangedKeys.Count > 0
            && !message.ChangedKeys.Contains(EntitlementKeys.GuildPublicListing.Name)) return;

        // EntitlementCacheHandler races this handler with no ordering guarantee - invalidate first
        // so a win by this handler cannot read the still-fresh pre-change cached set. Idempotent if
        // the cache handler also runs.
        await cache.InvalidateAsync(EntitlementSubject.ForGuild(message.SubjectId), ct);

        var set = await entitlements.ResolveAsync(EntitlementSubject.ForGuild(message.SubjectId), ct);

        // Only the plan saying false suspends. Anything ambiguous does nothing and waits for the
        // next event: under-suspending recovers on the next event, over-suspending never does.
        if (set.Flag(EntitlementKeys.GuildPublicListing)) return;
        if (set.ProvenanceOf(EntitlementKeys.GuildPublicListing).Source == EntitlementPrecedence.CatalogueDefault) return;

        var listing = await ctx.Listings.FirstOrDefaultAsync(l => l.GuildId == message.SubjectId, ct);
        if (listing is null) return;

        // Suspend no-ops off anything but Published, so a Draft is left alone - captured before the
        // call rather than compared after, so an already-Suspended listing does not push a second time.
        var wasPublished = listing.State == ListingState.Published;
        listing.Suspend(SuspensionReason.PlanLapsed);
        if (!wasPublished) return;

        await realtime.ListingSuspendedAsync(listing, ct);
    }
}
