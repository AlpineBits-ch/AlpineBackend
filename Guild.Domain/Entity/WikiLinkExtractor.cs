using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Guild.Domain.Entity;

/// <summary>One internal link found in a page body.</summary>
/// <param name="TargetPageId">The page the link points at, which need not exist.</param>
/// <param name="HeadingId">The heading slug after the '#', or null.</param>
public readonly record struct WikiPageReference(string TargetPageId, string? HeadingId);

/// <summary>Finds the pages a wiki page's markdown links to.</summary>
public static class WikiLinkExtractor
{
    /// <summary>The scheme an internal link is written with: <c>[Title](wiki:wkpg_abc)</c>.</summary>
    public const string Scheme = "wiki:";

    /// <summary>How many edges one page may contribute to the graph.</summary>
    public const int MaxLinksPerPage = 250;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>
    /// The internal links in <paramref name="content"/>, deduped on (target, heading) in
    /// first-appearance order and capped at <see cref="MaxLinksPerPage"/>.
    /// </summary>
    /// <param name="content">The page body as markdown.</param>
    /// <param name="sourcePageId">The page being parsed, whose own id is not a link.</param>
    /// <returns>The pages this body points at.</returns>
    public static List<WikiPageReference> Extract(string? content, string? sourcePageId = null)
    {
        // Short-circuit before parsing.
        if (string.IsNullOrWhiteSpace(content)) return [];
        if (!content.Contains(Scheme, StringComparison.Ordinal)) return [];

        var document = Markdown.Parse(content, Pipeline);
        var found = new List<WikiPageReference>();
        var seen = new HashSet<(string, string?)>();

        // Code exclusion is structural rather than a check in this loop, and worth being explicit
        // about because it looks like an omission: Markdig does not run inline parsing inside
        // fenced or indented code blocks, and a code span is a leaf that cannot contain a link.
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage) continue;
            if (!TryParse(link.Url, out var reference)) continue;
            if (string.Equals(reference.TargetPageId, sourcePageId, StringComparison.Ordinal)) continue;
            if (!seen.Add((reference.TargetPageId, reference.HeadingId))) continue;

            found.Add(reference);
            if (found.Count == MaxLinksPerPage) break;
        }

        return found;
    }

    private static bool TryParse(string? url, out WikiPageReference reference)
    {
        reference = default;

        if (url is null || !url.StartsWith(Scheme, StringComparison.Ordinal)) return false;

        var rest = url[Scheme.Length..];
        var hash = rest.IndexOf('#', StringComparison.Ordinal);

        var target = (hash < 0 ? rest : rest[..hash]).Trim();
        if (target.Length == 0) return false;

        var heading = hash < 0 ? null : rest[(hash + 1)..].Trim();

        reference = new WikiPageReference(target, string.IsNullOrEmpty(heading) ? null : heading);
        return true;
    }
}
