using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Echo.Wiki;

/// <summary>Where a public page's links and images are allowed to point.</summary>
/// <param name="ImageHosts">Hostnames an image may be loaded from.</param>
/// <param name="InstanceBaseUrl">
/// What an app-relative URL is rebased onto. A leading slash would otherwise resolve against the
/// wiki host, which serves no media and no application routes.
/// </param>
public sealed record WikiLinkPolicy(IReadOnlyCollection<string> ImageHosts, string InstanceBaseUrl);

/// <summary>
/// Turns a wiki page's markdown into the HTML the public site serves.
///
/// Sanitisation is structural rather than a filter over a string of HTML: the parser is built with
/// raw HTML support removed, so a page containing <c>&lt;script&gt;</c> never produces an element in
/// the first place - it produces a literal that the renderer escapes. The only attacker-controlled
/// values that survive into attributes are link and image destinations, and those are checked
/// against an allowlist here. The site's CSP is the second line, not the first.
/// </summary>
public static class WikiMarkdown
{
    /// <summary>What a link may point at once it is rendered.</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>Untrusted outbound links carry no ranking value and no window handle.</summary>
    private const string OutboundRel = "nofollow ugc noopener noreferrer";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        // Drops the HTML block parser and turns off inline HTML parsing, which is the whole
        // sanitisation story: nothing downstream has to recognise a dangerous tag, because no tag
        // the author writes ever becomes markup.
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    /// <summary>Renders a page body to HTML fit to serve anonymously.</summary>
    /// <param name="markdown">The page body as the author wrote it.</param>
    /// <param name="policy">Where links and images may point.</param>
    /// <returns>Sanitised HTML, or an empty string for an empty page.</returns>
    public static string Render(string? markdown, WikiLinkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var document = Markdown.Parse(markdown, Pipeline);

        // Materialised before mutating: removing a node mid-traversal corrupts the walk.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage) NeutraliseImage(link, policy);
            else NeutraliseLink(link, policy);
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

    /// <summary>A one-line plain-text summary, for the description and Open Graph tags.</summary>
    /// <param name="markdown">The page body as the author wrote it.</param>
    /// <param name="maxLength">Where to cut, in characters.</param>
    /// <returns>Collapsed plain text, truncated on a word boundary where there is one.</returns>
    public static string Summarise(string? markdown, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var text = Markdown.ToPlainText(markdown, Pipeline);
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length <= maxLength) return collapsed;

        var cut = collapsed[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');

        return (lastSpace > maxLength / 2 ? cut[..lastSpace] : cut).TrimEnd() + "...";
    }

    /// <summary>
    /// Resolves an image URL to something safe to put in a src attribute, or null to drop it. An
    /// image the instance does not host is removed rather than rewritten: a public page must not
    /// fetch anything from an address its author chose, because every anonymous view would hand that
    /// address a visitor's IP and user agent.
    /// </summary>
    /// <param name="url">The image URL as written.</param>
    /// <param name="policy">Where images may point.</param>
    /// <returns>The URL to serve, or null when the image must not be rendered.</returns>
    public static string? ResolveImage(string? url, WikiLinkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var value = Clean(url);
        if (value.Length == 0) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) return null;
        if (value[0] == '/') return policy.InstanceBaseUrl + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        return policy.ImageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ? value : null;
    }

    private static void NeutraliseImage(LinkInline image, WikiLinkPolicy policy)
    {
        if (ResolveImage(image.Url, policy) is { } resolved) image.Url = resolved;
        else image.Remove();
    }

    private static void NeutraliseLink(LinkInline link, WikiLinkPolicy policy)
    {
        var url = Clean(link.Url);

        if (!IsAllowedHref(url))
        {
            // Kept as an anchor so the author's link text survives, pointed at nothing.
            link.Url = "#";
            return;
        }

        // A bare relative destination resolves against this page, which is how one wiki page links
        // to its sibling; only the rooted ones have to be rebased.
        link.Url = url[0] == '/' ? policy.InstanceBaseUrl + url : url;

        if (!IsAbsolute(link.Url)) return;

        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("rel", OutboundRel);
        attributes.AddPropertyIfNotExist("target", "_blank");
    }

    /// <summary>
    /// Strips whitespace and control characters before anything looks at the scheme. Browsers ignore
    /// a tab inside <c>java&#9;script:</c>; a parser that does not would let it through.
    /// </summary>
    private static string Clean(string? url) =>
        url is null ? string.Empty : new string([.. url.Where(c => !char.IsControl(c))]).Trim();

    private static bool IsAllowedHref(string url)
    {
        if (url.Length == 0) return false;

        // Protocol-relative is an absolute URL wearing a relative URL's clothes.
        if (url.StartsWith("//", StringComparison.Ordinal)) return false;

        if (url[0] is '#' or '/') return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

        // A destination with no scheme at all is a relative path; one with a colon it could not
        // parse is not something to guess about.
        return !url.Contains(':', StringComparison.Ordinal);
    }

    private static bool IsAbsolute(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
