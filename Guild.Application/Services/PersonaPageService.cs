using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The two things a character page does that an ordinary wiki page does not: it is copied out of
/// whichever guild copy is currently the reference, and it can be pulled from that copy again
/// later without losing what this guild's moderators wrote in the meantime.
/// </summary>
public class PersonaPageService(MicroserviceContext ctx)
{
    /// <summary>
    /// How this guild's copy stands against the reference copy: <c>current</c>, <c>behind</c>, or
    /// <c>diverged</c> for a copy whose upstream history no longer exists.
    /// </summary>
    public async Task<string> ResolveUpstreamStateAsync(Persona persona, PersonaGuildProfile profile)
    {
        if (profile.WikiPageId is null) return PersonaUpstreamState.Current;
        if (persona.HomeProfileId is null || persona.HomeProfileId == profile.Id) return PersonaUpstreamState.Current;

        // Promotion nulls the number rather than rewriting it, because the remaining copies hold
        // revision numbers from a history they never shared.
        if (profile.UpstreamRevisionNumber is null) return PersonaUpstreamState.Diverged;

        var reference = await LoadReferenceAsync(persona);
        if (reference is null) return PersonaUpstreamState.Diverged;

        var latest = await LatestRevisionNumberAsync(reference.Id);
        return latest > profile.UpstreamRevisionNumber ? PersonaUpstreamState.Behind : PersonaUpstreamState.Current;
    }

    /// <summary>The reference copy's wiki page, or null when the persona has no home profile.</summary>
    public async Task<WikiPage?> LoadReferenceAsync(Persona persona)
    {
        if (persona.HomeProfileId is null) return null;

        var pageId = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.Id == persona.HomeProfileId)
            .Select(p => p.WikiPageId)
            .FirstOrDefaultAsync();

        if (pageId is null) return null;

        return await ctx.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId);
    }

    public async Task<int> LatestRevisionNumberAsync(string pageId) =>
        await ctx.WikiRevisions
            .AsNoTracking()
            .Where(r => r.PageId == pageId)
            .Select(r => (int?)r.RevisionNumber)
            .MaxAsync() ?? 0;

    /// <summary>
    /// Creates this guild's character page, seeded from the reference copy when there is one, and
    /// links it to the profile.
    /// </summary>
    public async Task<WikiPage> CreatePageAsync(
        Persona persona, PersonaGuildProfile profile, string title, string? categoryId, string authorId)
    {
        var reference = await LoadReferenceAsync(persona);

        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = profile.GuildId,
            Title = title,
            Content = reference?.Content ?? string.Empty,
            AuthorId = authorId,
            CategoryId = categoryId,
            Tags = reference?.Tags is { Count: > 0 } tags ? [.. tags] : [],
            Icon = reference?.Icon,
            CoverUrl = reference?.CoverUrl,
            PersonaId = persona.Id,
            InfoboxJson = reference?.InfoboxJson,
        });

        ctx.WikiPages.Add(page);

        profile.WikiPageId = page.Id;
        profile.UpstreamRevisionNumber = reference is null ? null : await LatestRevisionNumberAsync(reference.Id);
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        // The first copy made anywhere becomes the reference, so a character always has one and is
        // never hostage to whichever guild happened to be first.
        persona.HomeProfileId ??= profile.Id;

        return page;
    }

    /// <summary>
    /// Merges the reference copy into this guild's copy. A preview writes nothing, and every other
    /// strategy still leaves the caller the diff it applied.
    /// </summary>
    public async Task<PersonaPagePullDto> PullAsync(
        Persona persona, PersonaGuildProfile profile, PersonaPagePullStrategy strategy, string editorId)
    {
        var result = new PersonaPagePullDto
        {
            PersonaId = persona.Id,
            PageId = profile.WikiPageId,
            UpstreamRevisionNumber = profile.UpstreamRevisionNumber,
            UpstreamState = await ResolveUpstreamStateAsync(persona, profile),
        };

        var local = profile.WikiPageId is null
            ? null
            : await ctx.WikiPages.Include(p => p.Revisions).FirstOrDefaultAsync(p => p.Id == profile.WikiPageId);

        var reference = await LoadReferenceAsync(persona);
        if (local is null || reference is null || reference.Id == local.Id)
        {
            result.MergedContent = local?.Content ?? string.Empty;
            result.MergedInfoboxJson = local?.InfoboxJson;
            return result;
        }

        result.ReferenceRevisionNumber = await LatestRevisionNumberAsync(reference.Id);

        var baseContent = profile.UpstreamRevisionNumber is null
            ? null
            : await ctx.WikiRevisions
                .AsNoTracking()
                .Where(r => r.PageId == reference.Id && r.RevisionNumber == profile.UpstreamRevisionNumber)
                .Select(r => r.Content)
                .FirstOrDefaultAsync();

        var merge = strategy == PersonaPagePullStrategy.TakeUpstream
            ? new MergeOutcome(reference.Content, [])
            : ThreeWayMerge(baseContent, local.Content, reference.Content);

        result.MergedContent = merge.Content;
        result.Conflicts = merge.Conflicts;

        // The infobox is opaque JSON, so there is nothing to merge line by line: upstream wins only
        // where this guild never wrote one.
        result.MergedInfoboxJson = strategy == PersonaPagePullStrategy.TakeUpstream
            ? reference.InfoboxJson
            : local.InfoboxJson ?? reference.InfoboxJson;

        if (strategy == PersonaPagePullStrategy.Preview) return result;

        if (merge.Content != local.Content || result.MergedInfoboxJson != local.InfoboxJson)
        {
            local.Content = merge.Content;
            local.InfoboxJson = result.MergedInfoboxJson;
            local.LastEditorId = editorId;

            var nextRevision = local.Revisions.Count > 0 ? local.Revisions.Max(r => r.RevisionNumber) + 1 : 1;
            ctx.WikiRevisions.Add(WikiRevision.Create(new CreateWikiRevisionParams
            {
                PageId = local.Id,
                Content = local.Content,
                InfoboxJson = local.InfoboxJson,
                EditorId = editorId,
                RevisionNumber = nextRevision,
                Summary = $"Pulled from the reference copy at revision #{result.ReferenceRevisionNumber}",
            }));

            local.RaiseUpdated(editorId);
        }

        profile.UpstreamRevisionNumber = result.ReferenceRevisionNumber;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        result.Applied = true;
        result.UpstreamState = PersonaUpstreamState.Current;
        return result;
    }

    private sealed record MergeOutcome(string Content, List<PersonaPageConflictDto> Conflicts);

    /// <summary>
    /// A line-oriented three-way merge that defaults to keeping local edits: upstream's version of
    /// a line is taken only where this guild left the line as it was copied. Every line both sides
    /// changed stays local and is reported, because a pull that fast-forwards silently discards a
    /// moderator's work.
    /// </summary>
    private static MergeOutcome ThreeWayMerge(string? baseContent, string local, string upstream)
    {
        // With no common ancestor there is nothing to merge against, so local stands and the whole
        // upstream body is one conflict for the caller to look at.
        if (baseContent is null)
        {
            return new MergeOutcome(local, local == upstream
                ? []
                : [new PersonaPageConflictDto { LineNumber = 0, Local = local, Upstream = upstream }]);
        }

        var baseLines = SplitLines(baseContent);
        var localLines = SplitLines(local);
        var upstreamLines = SplitLines(upstream);

        var merged = new List<string>();
        var conflicts = new List<PersonaPageConflictDto>();
        var count = Math.Max(baseLines.Length, Math.Max(localLines.Length, upstreamLines.Length));

        for (var i = 0; i < count; i++)
        {
            var b = i < baseLines.Length ? baseLines[i] : null;
            var l = i < localLines.Length ? localLines[i] : null;
            var u = i < upstreamLines.Length ? upstreamLines[i] : null;

            if (l == u)
            {
                if (l is not null) merged.Add(l);
                continue;
            }

            if (l == b)
            {
                if (u is not null) merged.Add(u);
                continue;
            }

            if (u == b)
            {
                if (l is not null) merged.Add(l);
                continue;
            }

            if (l is not null) merged.Add(l);
            conflicts.Add(new PersonaPageConflictDto { LineNumber = i + 1, Base = b, Local = l, Upstream = u });
        }

        return new MergeOutcome(string.Join("\n", merged), conflicts);
    }

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n").Split('\n');
}
