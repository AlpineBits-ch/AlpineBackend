using System.Linq.Expressions;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>
/// Bans a guild out of the discovery directory. Guild-keyed, not listing-keyed - see the class doc
/// on <see cref="DiscoveryBan"/> for why. Active is evaluated on read: no sweeper, so a temporary
/// ban cannot be left hanging by a background job that failed to run. Every method takes "now"
/// explicitly rather than holding a clock, so a caller that already has one (a Wolverine handler
/// with TimeProvider injected) supplies a single consistent instant.
/// </summary>
public class DiscoveryBanService(MicroserviceContext ctx, ListingRealtime realtime)
{
    /// <summary>
    /// The guild's currently active ban, or null. Returns the entity rather than a bool because the
    /// caller that matters most - <see cref="ListingWriteService.PublishAsync"/> - needs the
    /// owner-facing <see cref="DiscoveryBan.Reason"/> for its refusal, and a second query for that
    /// would just repeat this same predicate.
    /// </summary>
    public Task<DiscoveryBan?> IsBannedAsync(string guildId, DateTimeOffset now, CancellationToken ct) =>
        ctx.DiscoveryBans.Where(Active(guildId, now)).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Suspends a published listing with <see cref="SuspensionReason.StaffAction"/> and pushes the
    /// existing <see cref="ListingRealtime.ListingSuspendedAsync"/> event - only when the listing was
    /// actually published, since <see cref="Listing.Suspend"/> no-ops otherwise and there is nothing
    /// to tell anyone.
    /// </summary>
    public async Task<DiscoveryBan> BanAsync(
        string guildId, string reason, string? staffNote, string byUserId,
        DateTimeOffset now, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var ban = DiscoveryBan.Create(guildId, reason, staffNote, byUserId, now, expiresAt);
        ctx.DiscoveryBans.Add(ban);

        var listing = await ctx.Listings.FirstOrDefaultAsync(l => l.GuildId == guildId, ct);
        if (listing is not null && listing.State == ListingState.Published)
        {
            listing.Suspend(SuspensionReason.StaffAction);
            await realtime.ListingSuspendedAsync(listing, ct);
        }

        return ban;
    }

    /// <summary>
    /// Lifts every currently active ban for the guild - normally one row, but nothing enforces that
    /// uniquely. Never touches the listing: republishing after a staff ban is the owner's decision,
    /// the same rule as a lapsed plan.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveryBan>> LiftAsync(string guildId, string byUserId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await ctx.DiscoveryBans.Where(Active(guildId, now)).ToListAsync(ct);
        foreach (var ban in active)
        {
            ban.LiftedAt = now;
            ban.LiftedByUserId = byUserId;
        }

        return active;
    }

    /// <summary>Every ban, most recent first. Active-only by default; <paramref name="includeLifted"/>
    /// adds the history. <paramref name="guildId"/> narrows to one guild - used both by the admin
    /// console's per-guild view and internally to find the reason behind a suspended listing.</summary>
    public Task<List<DiscoveryBan>> ListAsync(string? guildId, bool includeLifted, DateTimeOffset now, CancellationToken ct)
    {
        var query = ctx.DiscoveryBans.AsNoTracking().AsQueryable();
        if (guildId is not null) query = query.Where(b => b.GuildId == guildId);
        if (!includeLifted) query = query.Where(ActiveOnly(now));

        return query.OrderByDescending(b => b.BannedAt).ToListAsync(ct);
    }

    private static Expression<Func<DiscoveryBan, bool>> Active(string guildId, DateTimeOffset now) =>
        b => b.GuildId == guildId && b.LiftedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now);

    private static Expression<Func<DiscoveryBan, bool>> ActiveOnly(DateTimeOffset now) =>
        b => b.LiftedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now);
}
