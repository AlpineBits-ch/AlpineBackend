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
public class DiscoveryBanHandlers
{
    public static async Task<BanGuildFromDiscoveryResponse> Handle(
        BanGuildFromDiscoveryRequest request, DiscoveryBanService bans, TimeProvider clock, CancellationToken ct)
    {
        var ban = await bans.BanAsync(
            request.GuildId, request.Reason, request.StaffNote, request.StaffUserId,
            clock.GetUtcNow(), request.ExpiresAt, ct);

        return new BanGuildFromDiscoveryResponse { BanId = ban.Id };
    }

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
    /// Reuses DiscoveryFeedQuery's candidate filter rather than a second text-search query - the
    /// ranking that query builds on top is skipped, this just orders by recency.</summary>
    public static async Task<SearchDiscoveryListingsResponse> Handle(
        SearchDiscoveryListingsRequest request, MicroserviceContext ctx, GuildProfileMirror mirror, CancellationToken ct)
    {
        const int pageSize = 20;

        var candidates = await DiscoveryFeedQuery.PublishedCandidatesQuery(ctx, language: null, request.Query)
            .ToListAsync(ct);

        var ordered = candidates
            .OrderByDescending(l => l.PublishedAt)
            .ThenBy(l => l.Id, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var cursorIndex = ordered.FindIndex(l => l.Id == request.Cursor);
            if (cursorIndex >= 0) ordered = ordered.Skip(cursorIndex + 1).ToList();
        }

        var window = ordered.Take(pageSize + 1).ToList();
        var hasMore = window.Count > pageSize;
        var page = hasMore ? window.Take(pageSize).ToList() : window;

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
