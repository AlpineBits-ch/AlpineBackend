using Discovery.Api.Dtos.Response;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>
/// A user's own interest set: what ranking (task 12) reads, and what the profile editor writes.
/// ReplaceAsync mutates the tracked context and returns without saving - same contract as
/// TopicResolver.EnsureTagsAsync and GuildProfileMirror.EnsureFreshAsync, since the caller's
/// Wolverine endpoint commits on return.
/// </summary>
public class InterestService(MicroserviceContext ctx, TopicResolver resolver)
{
    public const int MaxInterests = 25;

    public async Task<InterestsDto> GetAsync(string userId, CancellationToken ct)
    {
        var rows = await ctx.UserInterests.Where(i => i.UserId == userId).ToListAsync(ct);
        var visibility = await ctx.InterestVisibilities.FirstOrDefaultAsync(v => v.UserId == userId, ct);

        var topics = await resolver.ResolveAsync(rows.Select(r => new TopicRef(r.Kind, r.TopicId)), ct);
        return new InterestsDto { Topics = topics, Visible = visibility?.Visible ?? true };
    }

    /// <summary>
    /// Replaces the caller's whole interest set. The cap bounds how many interests a user HAS, not
    /// how verbose the request was, so duplicates collapse first and the cap is checked against
    /// that deduplicated set - before any write, so a rejected PUT never leaves a half-applied set.
    /// A topic that stays listed keeps its existing row untouched, Source included; visibility never
    /// touches which topics are stored, only whether other people's view of the profile shows them.
    /// </summary>
    public async Task<InterestsDto> ReplaceAsync(
        string userId, IReadOnlyList<TopicInput> topics, bool visible, CancellationToken ct)
    {
        var distinct = topics.GroupBy(t => t.Topic).Select(g => g.First()).ToList();
        if (distinct.Count > MaxInterests)
            throw new ArgumentException($"At most {MaxInterests} interests are allowed.");

        // Games are never minted - unlike a tag, an unknown game id is a bad request, not a new
        // row. Checked before EnsureTagsAsync touches the context, so a request naming one game
        // that does not exist mints no tags either.
        var gameIds = distinct.Where(t => t.Topic.Kind == TopicKind.Game).Select(t => t.Topic.Id).ToList();
        if (gameIds.Count > 0)
        {
            var knownGameIds = await ctx.GameTopics
                .Where(g => gameIds.Contains(g.GameApplicationId))
                .Select(g => g.GameApplicationId)
                .ToListAsync(ct);
            var unknown = gameIds.Except(knownGameIds).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown topic: game:{unknown[0]}");
        }

        var minted = await resolver.EnsureTagsAsync(distinct, ct);

        var existing = await ctx.UserInterests.Where(i => i.UserId == userId).ToListAsync(ct);
        var requested = distinct.Select(t => t.Topic).ToHashSet();
        var alreadyPresent = existing.Select(i => new TopicRef(i.Kind, i.TopicId)).ToHashSet();

        foreach (var row in existing)
        {
            if (!requested.Contains(new TopicRef(row.Kind, row.TopicId)))
                ctx.UserInterests.Remove(row);
        }

        foreach (var input in distinct)
        {
            if (alreadyPresent.Contains(input.Topic)) continue;

            ctx.UserInterests.Add(new UserInterest
            {
                Id = UserInterest.GenerateId(),
                UserId = userId,
                Kind = input.Topic.Kind,
                TopicId = input.Topic.Id,
                Source = InterestSource.Manual,
            });
        }

        var visibilityRow = await ctx.InterestVisibilities.FirstOrDefaultAsync(v => v.UserId == userId, ct);
        if (visibilityRow is null)
        {
            ctx.InterestVisibilities.Add(new InterestVisibility
            {
                Id = InterestVisibility.GenerateId(),
                UserId = userId,
                Visible = visible,
            });
        }
        else
        {
            visibilityRow.Visible = visible;
        }

        return new InterestsDto { Topics = await DescribeAsync(requested, minted, ct), Visible = visible };
    }

    /// <summary>
    /// Resolves the requested topics for the response. ReplaceAsync never saves, so a tag
    /// EnsureTagsAsync just minted is not in the database yet and resolver.ResolveAsync alone would
    /// silently drop it - fall back to EnsureTagsAsync's own return for those instead of re-deriving
    /// it, so TopicResolver stays the only thing that knows how a tag becomes a row.
    /// </summary>
    private async Task<IReadOnlyList<TopicDto>> DescribeAsync(
        IReadOnlySet<TopicRef> refs, IReadOnlyList<TopicDto> mintedTags, CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(refs, ct);
        var byRef = resolved.ToDictionary(t =>
            new TopicRef(t.Kind == "game" ? TopicKind.Game : TopicKind.Tag, t.Id));

        var mintedByRef = mintedTags.ToDictionary(t => new TopicRef(TopicKind.Tag, t.Id));

        return refs.Select(r => byRef.TryGetValue(r, out var dto) ? dto : mintedByRef[r]).ToList();
    }
}
