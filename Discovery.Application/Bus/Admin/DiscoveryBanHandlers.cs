using Discovery.Api.Services;
using Discovery.Contracts.Bus.Admin;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Bus.Admin;

/// <summary>
/// The instance operator's discovery console, over the bus. Discovery stays free of any notion of
/// staff - the gateway's AdminDiscoveryController resolves the acting principal and forwards it as
/// a plain user id.
/// </summary>
/// <remarks>
/// Class name is singular - "DiscoveryBanHandlers" (plural) silently drops every Handle overload
/// from Wolverine's conventional discovery, confirmed against a clean codegen run. No other file in
/// this repo actually exercises a plural "...Handlers" class with a Task&lt;TResponse&gt; overload
/// successfully; do not rename this back.
/// </remarks>
public class DiscoveryBanHandler
{
    // Neither this method nor DiscoveryBanService saves. The commit still happens: Wolverine's
    // AutoApplyTransactions walks the constructor graph and finds MicroserviceContext transitively
    // through DiscoveryBanService, confirmed in the generated code (EfCoreEnvelopeTransaction wraps
    // this handler and calls SaveChangesAsync). Add MicroserviceContext as a direct parameter here
    // only if that generated frame ever disappears.
    public static async Task<BanGuildFromDiscoveryResponse> Handle(
        BanGuildFromDiscoveryRequest request, DiscoveryBanService bans, TimeProvider clock, CancellationToken ct)
    {
        var ban = await bans.BanAsync(
            request.GuildId, request.Reason, request.StaffNote, request.StaffUserId,
            clock.GetUtcNow(), request.ExpiresAt, ct);

        return new BanGuildFromDiscoveryResponse { BanId = ban.Id };
    }

    // See the Ban handler's comment above - same transitive-DbContext commit path.
    public static async Task<LiftDiscoveryBanResponse> Handle(
        LiftDiscoveryBanRequest request, DiscoveryBanService bans, TimeProvider clock, CancellationToken ct)
    {
        var lifted = await bans.LiftAsync(request.GuildId, request.StaffUserId, clock.GetUtcNow(), ct);
        return new LiftDiscoveryBanResponse { Lifted = lifted.Count > 0 };
    }

    public static async Task<ListDiscoveryBansResponse> Handle(
        ListDiscoveryBansRequest request, DiscoveryBanService bans, TimeProvider clock, CancellationToken ct)
    {
        var rows = await bans.ListAsync(guildId: null, request.IncludeLifted, clock.GetUtcNow(), ct);

        return new ListDiscoveryBansResponse
        {
            Bans = rows.Select(b => new DiscoveryBanSummary
            {
                Id = b.Id,
                GuildId = b.GuildId,
                Reason = b.Reason,
                StaffNote = b.StaffNote,
                BannedByUserId = b.BannedByUserId,
                BannedAt = b.BannedAt,
                ExpiresAt = b.ExpiresAt,
                LiftedAt = b.LiftedAt,
                LiftedByUserId = b.LiftedByUserId,
            }).ToList(),
        };
    }

    /// <summary>The console's search over published listings, to find the guild before banning it.
    /// A plain Id keyset cursor, not DiscoveryFeedQuery's ranked one - this is a lookup tool for
    /// staff, not the ranked feed, and it never reads Topics.</summary>
    public static async Task<SearchDiscoveryListingsResponse> Handle(
        SearchDiscoveryListingsRequest request, MicroserviceContext ctx, GuildProfileMirror mirror, CancellationToken ct)
    {
        const int pageSize = 20;

        var query = ctx.Listings.AsNoTracking().Where(l => l.State == ListingState.Published);

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var term = request.Query.Trim().ToLowerInvariant();
            query = query.Where(l => l.Headline.ToLower().Contains(term) || l.Pitch.ToLower().Contains(term));
        }

        if (!string.IsNullOrEmpty(request.Cursor))
            query = query.Where(l => string.Compare(l.Id, request.Cursor) > 0);

        // Bounded in SQL: an unscoped admin search must not pull every published listing in the
        // instance into memory just to page it.
        var page = await query.OrderBy(l => l.Id).Take(pageSize + 1).ToListAsync(ct);

        var hasMore = page.Count > pageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var profiles = await mirror.EnsureFreshAsync(page.Select(l => l.GuildId).Distinct().ToList(), ct);

        return new SearchDiscoveryListingsResponse
        {
            Listings = page.Select(l => new DiscoveryListingSummary
            {
                GuildId = l.GuildId,
                GuildName = profiles.TryGetValue(l.GuildId, out var profile) ? profile.Name : string.Empty,
                Headline = l.Headline,
                State = l.State.ToString(),
                PublishedAt = l.PublishedAt,
            }).ToList(),
            NextCursor = hasMore ? page[^1].Id : null,
        };
    }
}
