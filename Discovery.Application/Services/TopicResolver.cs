using System.Text;
using Discovery.Api.Dtos.Response;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>
/// The one seam where a <see cref="TopicRef"/> or a search string becomes a resolved topic. Games
/// come from the mirrored catalog (<see cref="MicroserviceContext.GameTopics"/>), tags from
/// <see cref="MicroserviceContext.Tags"/> with AliasOf followed once. Every later caller resolves
/// through this class rather than querying either table directly (spec section 16).
/// </summary>
public class TopicResolver(MicroserviceContext ctx)
{
    /// <summary>
    /// Filters against the database, then hands off to <see cref="RankOrder"/> for ordering.
    /// </summary>
    public async Task<IReadOnlyList<TopicDto>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var trimmed = query.Trim();

        // At least 10x the requested limit reaches memory, so RankOrder still has enough candidates
        // to pick from once games and tags are merged. It is the WHERE clause in
        // GameCandidatesQuery/TagCandidatesQuery, not this cap, that keeps an autocomplete keystroke
        // from pulling the whole catalog (tens of thousands of rows at the seeded size) over the wire.
        var candidateCap = Math.Max(limit * 10, 50);

        var games = await GameCandidatesQuery(ctx, trimmed).Take(candidateCap).ToListAsync(ct);
        var tags = await TagCandidatesQuery(ctx, trimmed).Take(candidateCap).ToListAsync(ct);

        var candidates = new List<TopicDto>();
        candidates.AddRange(games.Select(g =>
            new TopicDto { Kind = "game", Id = g.GameApplicationId, Name = g.Name, SteamAppId = g.SteamAppId }));
        candidates.AddRange(tags.Select(t => new TopicDto { Kind = "tag", Id = t.Slug, Name = t.DisplayName }));

        return RankOrder(candidates, trimmed).Take(limit).ToList();
    }

    /// <summary>
    /// The SQL-side game filter: enabled, and SearchText contains <paramref name="query"/>
    /// (case-insensitively). Public and static so a translation test can call ToQueryString() on it
    /// without a live database.
    ///
    /// Filters on the denormalized GameTopic.SearchText (name plus every alias, already
    /// lower-invariant) rather than on Name and Aliases separately: a nested lambda over the
    /// Aliases array translates fine on Npgsql but EF's InMemory provider refuses any method call
    /// inside it, so a substring match over the array could not be one query that also runs under
    /// InMemory (this project's own tests exercise SearchAsync against InMemory). SearchText is a
    /// scalar column, so one Contains() call on it has no such problem on either provider - and it
    /// is what the trigram index in the TrigramSearchTextColumn migration actually covers.
    /// </summary>
    public static IQueryable<GameTopic> GameCandidatesQuery(MicroserviceContext ctx, string query)
    {
        var term = query.ToLowerInvariant();
        return ctx.GameTopics.Where(g => g.IsEnabled && g.SearchText.Contains(term));
    }

    /// <summary>The SQL-side tag filter: not aliased away, display name or slug contains
    /// <paramref name="query"/> case-insensitively.</summary>
    public static IQueryable<Tag> TagCandidatesQuery(MicroserviceContext ctx, string query)
    {
        var term = query.ToLowerInvariant();
        return ctx.Tags.Where(t => t.AliasOf == null &&
            (t.DisplayName.ToLower().Contains(term) || t.Slug.Contains(term)));
    }

    /// <summary>
    /// Pure ordering: games before tags always, then the closer text match first. No database, no
    /// clock - a real search would rank the within-kind order by pg_trgm similarity, which EF's
    /// InMemory provider cannot evaluate, so tests call this directly with hand-built candidates
    /// instead of going through a query InMemory could not run.
    /// </summary>
    public static IReadOnlyList<TopicDto> RankOrder(IReadOnlyList<TopicDto> candidates, string query)
    {
        var trimmed = query.Trim();
        return candidates
            .OrderBy(c => c.Kind == "game" ? 0 : 1)
            .ThenByDescending(c => MatchScore(c.Name, trimmed))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int MatchScore(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    /// <summary>
    /// Turns references into rows. A disabled game still resolves here - only SearchAsync excludes
    /// it - because a listing already carrying a game that left the catalog must keep rendering its
    /// chip (see GameCatalogSync). A tag whose AliasOf is set resolves to its target.
    /// </summary>
    public async Task<IReadOnlyList<TopicDto>> ResolveAsync(IEnumerable<TopicRef> topics, CancellationToken ct)
    {
        var refs = topics.Distinct().ToList();
        var results = new List<TopicDto>();
        if (refs.Count == 0) return results;

        var gameIds = refs.Where(t => t.Kind == TopicKind.Game).Select(t => t.Id).Distinct().ToList();
        if (gameIds.Count > 0)
        {
            var games = await ctx.GameTopics.Where(g => gameIds.Contains(g.GameApplicationId)).ToListAsync(ct);
            results.AddRange(games.Select(g =>
                new TopicDto { Kind = "game", Id = g.GameApplicationId, Name = g.Name, SteamAppId = g.SteamAppId }));
        }

        var tagSlugs = refs.Where(t => t.Kind == TopicKind.Tag).Select(t => t.Id).Distinct().ToList();
        if (tagSlugs.Count > 0)
        {
            var tags = await ctx.Tags.Where(t => tagSlugs.Contains(t.Slug)).ToListAsync(ct);

            // AliasOf stores the target's slug, the same key everything else here uses. Staff
            // re-point a merge directly at its target rather than chaining, so one hop is enough.
            var targetSlugs = tags.Where(t => t.AliasOf != null).Select(t => t.AliasOf!).Distinct().ToList();
            var targets = targetSlugs.Count == 0
                ? []
                : await ctx.Tags.Where(t => targetSlugs.Contains(t.Slug)).ToListAsync(ct);
            var targetsBySlug = targets.ToDictionary(t => t.Slug);

            foreach (var tag in tags)
            {
                var resolved = tag.AliasOf != null && targetsBySlug.TryGetValue(tag.AliasOf, out var target)
                    ? target
                    : tag;
                results.Add(new TopicDto { Kind = "tag", Id = resolved.Slug, Name = resolved.DisplayName });
            }
        }

        return results;
    }

    /// <summary>
    /// Mints a Tag row for any tag TopicInput with no existing row, and returns a resolved DTO for
    /// every distinct tag topic passed in - including the ones it just minted. Mutates the tracked
    /// context and returns without saving - the caller's Wolverine endpoint commits, same contract
    /// as GuildProfileMirror.EnsureFreshAsync - so a freshly minted row is not yet queryable and a
    /// caller needing it in the same call must read it off this return value, not re-query.
    ///
    /// An existing tag's DisplayName is never touched here: first writer wins, and a staff merge is
    /// the tool for fixing a bad one, not a later caller's casing. This does not follow AliasOf -
    /// that is ResolveAsync's job, and it always finds anything already saved.
    /// </summary>
    public async Task<IReadOnlyList<TopicDto>> EnsureTagsAsync(IEnumerable<TopicInput> topics, CancellationToken ct)
    {
        var candidates = topics
            .Where(t => t.Topic.Kind == TopicKind.Tag)
            .GroupBy(t => t.Topic.Id)
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0) return [];

        var slugs = candidates.Select(c => c.Topic.Id).ToList();
        var existing = await ctx.Tags.Where(t => slugs.Contains(t.Slug)).ToListAsync(ct);
        var bySlug = existing.ToDictionary(t => t.Slug);

        var results = new List<TopicDto>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!bySlug.TryGetValue(candidate.Topic.Id, out var tag))
            {
                // Tag has no static factory (unlike GameTopic/GuildProfile) - Id is set explicitly
                // here, the same trap task 6 hit: BaseEntity<T>.GenerateId() does not auto-populate
                // Id on save.
                tag = new Tag
                {
                    Id = Tag.GenerateId(),
                    Slug = candidate.Topic.Id,
                    DisplayName = DisplayNameFor(candidate),
                };
                ctx.Tags.Add(tag);
                bySlug[candidate.Topic.Id] = tag;
            }

            results.Add(new TopicDto { Kind = "tag", Id = tag.Slug, Name = tag.DisplayName });
        }

        return results;
    }

    private const int MaxDisplayNameLength = 80;

    /// <summary>
    /// DisplayName from what the caller actually typed: trimmed, internal whitespace collapsed to a
    /// single space, capped at Tag.DisplayName's column length (80). Falls back to the slug when
    /// there is no raw text.
    /// </summary>
    private static string DisplayNameFor(TopicInput input)
    {
        var raw = input.RawText?.Trim();
        if (string.IsNullOrEmpty(raw)) return input.Topic.Id;

        var collapsed = CollapseWhitespace(raw);
        return collapsed.Length > MaxDisplayNameLength
            ? collapsed[..MaxDisplayNameLength].TrimEnd()
            : collapsed;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
