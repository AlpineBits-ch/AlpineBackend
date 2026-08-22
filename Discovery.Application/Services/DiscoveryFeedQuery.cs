using Discovery.Api.Dtos.Response;
using Discovery.Domain.Entities;
using Discovery.Domain.Ranking;
using Discovery.Domain.Topics;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>What the feed endpoint asks for, assembled from the query string.</summary>
public record FeedRequest(
    string UserId,
    string? Query,
    IReadOnlyList<TopicRef> Topics,
    string? Language,
    string? Cursor,
    int Limit);

/// <summary>
/// The feed's one read path: interests in, ranked published listings out. Never writes - spec
/// section 16 keeps this and ListingWriteService as separate owners on purpose.
/// </summary>
public class DiscoveryFeedQuery(
    MicroserviceContext ctx,
    GuildProfileMirror mirror,
    TopicResolver resolver,
    TimeProvider clock)
{
    public async Task<DiscoveryFeedDto> RunAsync(FeedRequest request, CancellationToken ct)
    {
        // Public and defensive: the endpoint already clamps, but this class is a public seam in its
        // own right and Limit = 0 against a non-empty candidate set would make page[^1] below throw.
        var limit = Math.Clamp(request.Limit, 1, 50);

        var interestRows = await ctx.UserInterests
            .Where(i => i.UserId == request.UserId)
            .Select(i => new { i.Kind, i.TopicId })
            .ToListAsync(ct);
        var interests = interestRows.Select(r => (r.Kind, r.TopicId)).ToHashSet();

        var listingsQuery = PublishedCandidatesQuery(ctx, request.Language, request.Query);

        // OR across the requested set: a listing needs only one of the requested topics, not every
        // one of them. Under AND, every card on a filtered page would necessarily carry every
        // requested chip, which makes MatchedTopics informationally empty - the one thing spec 9.2
        // exists for is cards differing in what they matched. Grouped by kind rather than one
        // combined Contains over (kind, id) pairs: Contains() against two independent lists loses
        // the pairing between them and would match a Tag id against a Game-kind row.
        if (request.Topics.Count > 0)
        {
            var matchingListingIds = new List<string>();
            foreach (var group in request.Topics.GroupBy(t => t.Kind))
            {
                var ids = group.Select(t => t.Id).ToList();
                matchingListingIds.AddRange(await ListingIdsForTopicQuery(ctx, group.Key, ids).ToListAsync(ct));
            }

            var distinctIds = matchingListingIds.Distinct().ToList();
            listingsQuery = listingsQuery.Where(l => distinctIds.Contains(l.Id));
        }

        var candidates = await listingsQuery.ToListAsync(ct);

        var guildIds = candidates.Select(l => l.GuildId).Distinct().ToList();
        // Unrefreshed on purpose: reads whatever ActiveMemberCount is already mirrored locally, no
        // TTL check and no Guild call. A live check per candidate would mean one Guild round trip
        // per listing in the instance; only the final page gets that treatment, below.
        var localProfiles = await ctx.GuildProfiles
            .AsNoTracking()
            .Where(p => guildIds.Contains(p.GuildId))
            .ToDictionaryAsync(p => p.GuildId, ct);

        // A cursor carries the clock it was minted against - see FeedCursor's doc comment. Reusing
        // it keeps every page of one pagination session scored against the same instant; re-reading
        // the live clock per page would let freshness decay between requests and quietly defeat the
        // score+id tie-break the cursor exists to guarantee.
        var hasCursor = FeedCursor.TryDecode(request.Cursor, out var cursorScore, out var cursorId, out var cursorNow);
        var now = hasCursor ? cursorNow : clock.GetUtcNow();

        // v1 scores every filtered candidate in memory rather than in SQL. Fine while published
        // listings number in the low thousands; past that the fix is a materialized score column
        // refreshed on write and on bump, not a cleverer query.
        var scored = candidates.Select(listing =>
        {
            var listingTopics = listing.Topics.Select(t => (t.Kind, t.TopicId)).ToList();
            var matched = listingTopics.Where(interests.Contains).ToList();
            var activeMembers = localProfiles.TryGetValue(listing.GuildId, out var profile)
                ? profile.ActiveMemberCount
                : 0;

            var inputs = new RankInputs(matched.Count, listingTopics.Count, SinceBump(listing, now), activeMembers);

            return new ScoredListing(listing, ListingRank.Score(inputs),
                matched.Select(t => new TopicRef(t.Kind, t.TopicId)).ToList());
        }).ToList();

        IEnumerable<ScoredListing> ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Listing.Id, StringComparer.Ordinal);

        if (hasCursor)
        {
            ordered = ordered.SkipWhile(s =>
                s.Score > cursorScore || (s.Score == cursorScore && string.CompareOrdinal(s.Listing.Id, cursorId) <= 0));
        }

        // One extra row past the limit, just to know whether a next page exists.
        var window = ordered.Take(limit + 1).ToList();
        var hasMore = window.Count > limit;
        var page = hasMore ? window.Take(limit).ToList() : window;

        // Refreshed after paging, not before: a page of 24 cards refreshes at most 24 guild
        // profiles rather than every published listing in the instance.
        var pageGuildIds = page.Select(s => s.Listing.GuildId).Distinct().ToList();
        var freshProfiles = await mirror.EnsureFreshAsync(pageGuildIds, ct);

        var matchedRefs = page.SelectMany(s => s.MatchedTopics).Distinct().ToList();
        var resolvedTopics = await resolver.ResolveAsync(matchedRefs, ct);
        // GroupBy before the dictionary: ResolveAsync collapses an aliased tag onto its target
        // slug, so two distinct requested refs can resolve to the same TopicRef and collide on a
        // plain ToDictionary. Latent today, but a staff merge is exactly the tool that triggers it.
        var topicsByRef = resolvedTopics
            .GroupBy(t => new TopicRef(t.Kind == "game" ? TopicKind.Game : TopicKind.Tag, t.Id))
            .ToDictionary(g => g.Key, g => g.First());

        var cards = page.Select(s =>
        {
            freshProfiles.TryGetValue(s.Listing.GuildId, out var profile);
            return new DiscoveryCardDto
            {
                ListingId = s.Listing.Id,
                GuildId = s.Listing.GuildId,
                GuildName = profile?.Name ?? string.Empty,
                GuildIconUrl = profile?.IconUrl,
                GuildBannerUrl = profile?.BannerUrl,
                MemberCount = profile?.MemberCount ?? 0,
                Headline = s.Listing.Headline,
                Pitch = s.Listing.Pitch,
                Language = s.Listing.Language,
                JoinPolicy = s.Listing.JoinPolicy.ToString(),
                MatchedTopics = s.MatchedTopics
                    .Select(r => topicsByRef.TryGetValue(r, out var dto)
                        ? dto
                        : new TopicDto { Kind = r.Kind == TopicKind.Game ? "game" : "tag", Id = r.Id, Name = r.Id })
                    .ToList(),
            };
        }).ToList();

        var nextCursor = hasMore ? FeedCursor.Encode(page[^1].Score, page[^1].Listing.Id, now) : null;
        return new DiscoveryFeedDto { Cards = cards, NextCursor = nextCursor };
    }

    /// <summary>The SQL-side listing filter: published, matching language, and a headline/pitch
    /// text search - both sides lowercased so "chess" finds "Chess" (see
    /// TopicResolver.TagCandidatesQuery, same reason). AsNoTracking: a request-scoped read that gets
    /// scored and discarded has no business entering the change tracker. Public and static so a
    /// translation test can call ToQueryString() on it without a live database.</summary>
    public static IQueryable<Listing> PublishedCandidatesQuery(MicroserviceContext ctx, string? language, string? query)
    {
        var q = ctx.Listings.Include(l => l.Topics).AsNoTracking().Where(l => l.State == ListingState.Published);

        if (!string.IsNullOrWhiteSpace(language))
        {
            var lang = language.Trim();
            q = q.Where(l => l.Language == lang);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            q = q.Where(l => l.Headline.ToLower().Contains(term) || l.Pitch.ToLower().Contains(term));
        }

        return q;
    }

    /// <summary>Listing ids carrying any topic id in <paramref name="topicIds"/>, restricted to one
    /// kind per call - matching id lists across two different kinds independently would pair a tag
    /// id against a game-kind row. Public and static so a translation test can call ToQueryString()
    /// on it without a live database.</summary>
    public static IQueryable<string> ListingIdsForTopicQuery(
        MicroserviceContext ctx, TopicKind kind, IReadOnlyCollection<string> topicIds) =>
        ctx.ListingTopics.Where(t => t.Kind == kind && topicIds.Contains(t.TopicId)).Select(t => t.ListingId).Distinct();

    // Publish() always sets LastBumpedAt, so null here is an anomaly, not a case to score as
    // freshest - treat it as maximally stale rather than crash or favor it.
    private static TimeSpan SinceBump(Listing listing, DateTimeOffset now) =>
        listing.LastBumpedAt is { } bumped ? now - bumped : TimeSpan.MaxValue;

    private readonly record struct ScoredListing(Listing Listing, double Score, IReadOnlyList<TopicRef> MatchedTopics);
}
