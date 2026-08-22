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
        var interestRows = await ctx.UserInterests
            .Where(i => i.UserId == request.UserId)
            .Select(i => new { i.Kind, i.TopicId })
            .ToListAsync(ct);
        var interests = interestRows.Select(r => (r.Kind, r.TopicId)).ToHashSet();

        IQueryable<Listing> listingsQuery = ctx.Listings
            .Include(l => l.Topics)
            .Where(l => l.State == ListingState.Published);

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            var language = request.Language.Trim();
            listingsQuery = listingsQuery.Where(l => l.Language == language);
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var term = request.Query.Trim();
            listingsQuery = listingsQuery.Where(l => l.Headline.Contains(term) || l.Pitch.Contains(term));
        }

        // AND semantics: a listing must carry every requested topic. One Where(Any) per topic
        // rather than one combined Contains, because a method call inside a lambda nested inside
        // another lambda is exactly what EF's InMemory provider refuses to translate.
        foreach (var topic in request.Topics)
        {
            var kind = topic.Kind;
            var id = topic.Id;
            listingsQuery = listingsQuery.Where(l => l.Topics.Any(t => t.Kind == kind && t.TopicId == id));
        }

        var candidates = await listingsQuery.ToListAsync(ct);

        var guildIds = candidates.Select(l => l.GuildId).Distinct().ToList();
        // Unrefreshed on purpose: reads whatever ActiveMemberCount is already mirrored locally, no
        // TTL check and no Guild call. A live check per candidate would mean one Guild round trip
        // per listing in the instance; only the final page gets that treatment, below.
        var localProfiles = await ctx.GuildProfiles
            .Where(p => guildIds.Contains(p.GuildId))
            .ToDictionaryAsync(p => p.GuildId, ct);

        var now = clock.GetUtcNow();

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

        if (FeedCursor.TryDecode(request.Cursor, out var cursorScore, out var cursorId))
        {
            ordered = ordered.SkipWhile(s =>
                s.Score > cursorScore || (s.Score == cursorScore && string.CompareOrdinal(s.Listing.Id, cursorId) <= 0));
        }

        // One extra row past the limit, just to know whether a next page exists.
        var window = ordered.Take(request.Limit + 1).ToList();
        var hasMore = window.Count > request.Limit;
        var page = hasMore ? window.Take(request.Limit).ToList() : window;

        // Refreshed after paging, not before: a page of 24 cards refreshes at most 24 guild
        // profiles rather than every published listing in the instance.
        var pageGuildIds = page.Select(s => s.Listing.GuildId).Distinct().ToList();
        var freshProfiles = await mirror.EnsureFreshAsync(pageGuildIds, ct);

        var matchedRefs = page.SelectMany(s => s.MatchedTopics).Distinct().ToList();
        var resolvedTopics = await resolver.ResolveAsync(matchedRefs, ct);
        var topicsByRef = resolvedTopics.ToDictionary(t =>
            new TopicRef(t.Kind == "game" ? TopicKind.Game : TopicKind.Tag, t.Id));

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

        var nextCursor = hasMore ? FeedCursor.Encode(page[^1].Score, page[^1].Listing.Id) : null;
        return new DiscoveryFeedDto { Cards = cards, NextCursor = nextCursor };
    }

    // Publish() always sets LastBumpedAt, so null here is an anomaly, not a case to score as
    // freshest - treat it as maximally stale rather than crash or favor it.
    private static TimeSpan SinceBump(Listing listing, DateTimeOffset now) =>
        listing.LastBumpedAt is { } bumped ? now - bumped : TimeSpan.MaxValue;

    private readonly record struct ScoredListing(Listing Listing, double Score, IReadOnlyList<TopicRef> MatchedTopics);
}
