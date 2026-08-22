using Billing.Contracts.Bus.Events;
using Discovery.Api.Services;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Every key change on every plan raises this event - without both filters this resolves and
        // writes on unrelated billing traffic.
        if (message.SubjectKind != SubjectKind.Guild) return;
        if (!message.ChangedKeys.Contains(EntitlementKeys.GuildPublicListing.Name)) return;

        var set = await entitlements.ResolveAsync(EntitlementSubject.ForGuild(message.SubjectId), ct);

        // Still (or again) granted. A regain is never republished here - only the owner publishes.
        if (set.Flag(EntitlementKeys.GuildPublicListing)) return;

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
