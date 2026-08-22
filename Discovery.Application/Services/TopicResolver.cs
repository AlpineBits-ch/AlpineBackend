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
    /// Filters against the database, then hands off to <see cref="RankOrder"/> for ordering. Only
    /// IsEnabled/AliasOf are pushed to the database: Aliases is a Postgres array column and a nested
    /// Contains over its elements is not guaranteed to translate, so name/alias text matching runs
    /// over the materialized rows instead.
    /// </summary>
    public async Task<IReadOnlyList<TopicDto>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var trimmed = query.Trim();

        var games = await ctx.GameTopics.Where(g => g.IsEnabled).ToListAsync(ct);
        var tags = await ctx.Tags.Where(t => t.AliasOf == null).ToListAsync(ct);

        var candidates = new List<TopicDto>();

        candidates.AddRange(games
            .Where(g => Matches(g.Name, trimmed) || g.Aliases.Any(alias => Matches(alias, trimmed)))
            .Select(g => new TopicDto { Kind = "game", Id = g.GameApplicationId, Name = g.Name, SteamAppId = g.SteamAppId }));

        candidates.AddRange(tags
            .Where(t => Matches(t.DisplayName, trimmed))
            .Select(t => new TopicDto { Kind = "tag", Id = t.Slug, Name = t.DisplayName }));

        return RankOrder(candidates, trimmed).Take(limit).ToList();
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

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

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
    /// Mints a Tag row for any tag TopicRef with no existing row. Mutates the tracked context and
    /// returns without saving - the caller's Wolverine endpoint commits, same contract as
    /// GuildProfileMirror.EnsureFreshAsync.
    ///
    /// TopicRef.Id for a tag has already been through TagSlug.Normalize by the time it reaches this
    /// seam (TopicRef.Parse/TryParse do it internally - see TopicRefTests), so the caller's original
    /// casing and punctuation never survive to here. DisplayName is set from that same slug text,
    /// which is the only text this method has.
    /// </summary>
    public async Task EnsureTagsAsync(IEnumerable<TopicRef> topics, CancellationToken ct)
    {
        var candidates = topics
            .Where(t => t.Kind == TopicKind.Tag)
            .Select(t => new { Raw = t.Id, Slug = TagSlug.Normalize(t.Id) })
            .Where(t => t.Slug is not null)
            .GroupBy(t => t.Slug!)
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0) return;

        var slugs = candidates.Select(c => c.Slug!).ToList();
        var existing = await ctx.Tags.Where(t => slugs.Contains(t.Slug)).Select(t => t.Slug).ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        foreach (var candidate in candidates)
        {
            if (existingSet.Contains(candidate.Slug!)) continue;

            // Tag has no static factory (unlike GameTopic/GuildProfile) - Id is set explicitly here,
            // the same trap task 6 hit: BaseEntity<T>.GenerateId() does not auto-populate Id on save.
            ctx.Tags.Add(new Tag
            {
                Id = Tag.GenerateId(),
                Slug = candidate.Slug!,
                DisplayName = candidate.Raw,
            });
            existingSet.Add(candidate.Slug!);
        }
    }
}
