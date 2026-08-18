using System.Net;
using System.Text;

namespace Echo.Wiki;

/// <summary>What every rendered wiki document needs to know about this instance.</summary>
/// <param name="Scheme">The scheme the site is reached over.</param>
/// <param name="ApexHost">The hostname the wiki site is bound to.</param>
/// <param name="Links">Where a page's links and images may point.</param>
/// <param name="SupportUrl">Where a reader reports a page.</param>
/// <param name="InstanceName">What to call this instance in the chrome.</param>
public sealed record WikiRenderContext(
    string Scheme,
    string ApexHost,
    WikiLinkPolicy Links,
    string SupportUrl,
    string InstanceName)
{
    /// <summary>The origin one published wiki is canonically served from.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <returns>An absolute origin, with no trailing slash.</returns>
    public string OriginFor(string slug) => $"{Scheme}://{WikiHost.HostFor(slug, ApexHost)}";
}

/// <summary>
/// Renders the whole document server-side. There is no client script on this site at all: a
/// read-only page needs none, and the origin that renders arbitrary user prose is the last one that
/// should be running any.
/// </summary>
public static class WikiPageRenderer
{
    /// <summary>How much of a page becomes its description.</summary>
    private const int SummaryLength = 200;

    /// <summary>One published page.</summary>
    /// <param name="page">The page.</param>
    /// <param name="context">This instance's rendering settings.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Page(PublicWikiPage page, WikiRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        var summary = WikiMarkdown.Summarise(page.Content, SummaryLength);
        var body = new StringBuilder();

        body.Append("<article class=\"page\">");

        // Checked again here rather than trusted from the publish call: the column predates the
        // origin restriction, so a row can carry an address no current code would have accepted.
        if (WikiMarkdown.ResolveImage(page.CoverUrl, context.Links) is { } cover)
        {
            body.Append("<img class=\"cover\" src=\"").Append(Attribute(cover)).Append("\" alt=\"\" />");
        }

        body.Append("<h1>");
        if (page.Icon is { Length: > 0 }) body.Append("<span class=\"icon\">").Append(Text(page.Icon)).Append("</span> ");
        body.Append(Text(page.Title)).Append("</h1>");

        if (page.Category is { Length: > 0 })
            body.Append("<p class=\"meta\">").Append(Text(page.Category)).Append("</p>");

        body.Append("<div class=\"prose\">")
            .Append(WikiMarkdown.Render(page.Content, context.Links))
            .Append("</div>");

        body.Append("<p class=\"meta\">Last updated ")
            .Append(Text(page.UpdatedAt.UtcDateTime.ToString("d MMMM yyyy")))
            .Append("</p>");

        body.Append("</article>");

        // A wiki is served from its own subdomain, so a page's canonical address is a path on that
        // host. The apex still answers the path form, and pointing the canonical there instead would
        // leave one page with two addresses.
        return Document(
            title: $"{page.Title} - {page.GuildName}",
            description: summary,
            canonical: $"{context.OriginFor(page.WikiSlug)}/{page.Slug}",
            crumb: (page.GuildName, "/"),
            body: body.ToString(),
            context: context,
            reportRef: $"{page.WikiSlug}/{page.Slug}");
    }

    /// <summary>A published wiki's index.</summary>
    /// <param name="wiki">The wiki.</param>
    /// <param name="context">This instance's rendering settings.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Index(PublicWiki wiki, WikiRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(context);

        var body = new StringBuilder();

        body.Append("<article class=\"page\"><h1>").Append(Text(wiki.GuildName)).Append("</h1>");

        if (wiki.GuildDescription is { Length: > 0 })
            body.Append("<p class=\"lead\">").Append(Text(wiki.GuildDescription)).Append("</p>");

        if (wiki.Pages.Count == 0)
        {
            body.Append("<p class=\"meta\">This wiki has no published pages.</p>");
        }
        else
        {
            body.Append("<ul class=\"index\">");

            foreach (var page in wiki.Pages)
            {
                body.Append("<li><a href=\"/").Append(Attribute(page.Slug)).Append("\">");

                if (page.Icon is { Length: > 0 })
                    body.Append("<span class=\"icon\">").Append(Text(page.Icon)).Append("</span> ");

                body.Append(Text(page.Title)).Append("</a>");

                if (page.Category is { Length: > 0 })
                    body.Append("<span class=\"tag\">").Append(Text(page.Category)).Append("</span>");

                body.Append("</li>");
            }

            body.Append("</ul>");
        }

        body.Append("</article>");

        return Document(
            title: $"{wiki.GuildName} wiki",
            description: wiki.GuildDescription ?? $"The public wiki of {wiki.GuildName}.",
            canonical: context.OriginFor(wiki.Slug),
            crumb: null,
            body: body.ToString(),
            context: context,
            reportRef: wiki.Slug);
    }

    /// <summary>
    /// The one answer for a slug that does not exist, a wiki that was never published, a page that
    /// was not, and a page whose guild is gone. They must be indistinguishable.
    /// </summary>
    /// <param name="context">This instance's rendering settings.</param>
    /// <returns>A complete HTML document.</returns>
    public static string NotFound(WikiRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Document(
            title: "Not found",
            description: string.Empty,
            canonical: null,
            crumb: null,
            body: "<article class=\"page\"><h1>Not found</h1>"
                  + "<p class=\"lead\">There is no public page at this address.</p></article>",
            context: context);
    }

    /// <summary>Nobody answered, which is not the same as nothing being here.</summary>
    /// <param name="context">This instance's rendering settings.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Unavailable(WikiRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Document(
            title: "Temporarily unavailable",
            description: string.Empty,
            canonical: null,
            crumb: null,
            body: "<article class=\"page\"><h1>Temporarily unavailable</h1>"
                  + "<p class=\"lead\">This page could not be loaded. Try again in a moment.</p></article>",
            context: context);
    }

    /// <summary>The shell every document shares.</summary>
    private static string Document(
        string title,
        string description,
        string? canonical,
        (string Label, string Href)? crumb,
        string body,
        WikiRenderContext context,
        string? reportRef = null)
    {
        var html = new StringBuilder();

        html.Append("<!doctype html><html lang=\"en\"><head>");
        html.Append("<meta charset=\"utf-8\" />");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");

        // Indexing is a separate opt-in that does not exist yet, so every page carries this. A
        // public wiki is free SEO-bearing hosting on a real domain, and that is exactly what a spam
        // farm is shopping for.
        html.Append("<meta name=\"robots\" content=\"noindex, nofollow\" />");

        html.Append("<title>").Append(Text(title)).Append("</title>");

        if (description.Length > 0)
        {
            html.Append("<meta name=\"description\" content=\"").Append(Attribute(description)).Append("\" />");
            html.Append("<meta property=\"og:description\" content=\"").Append(Attribute(description)).Append("\" />");
        }

        html.Append("<meta property=\"og:title\" content=\"").Append(Attribute(title)).Append("\" />");
        html.Append("<meta property=\"og:type\" content=\"article\" />");
        html.Append("<meta property=\"og:site_name\" content=\"").Append(Attribute(context.InstanceName)).Append("\" />");

        if (canonical is not null)
        {
            html.Append("<link rel=\"canonical\" href=\"").Append(Attribute(canonical)).Append("\" />");
            html.Append("<meta property=\"og:url\" content=\"").Append(Attribute(canonical)).Append("\" />");
        }

        html.Append("<link rel=\"stylesheet\" href=\"/wiki.css\" />");
        html.Append("<link rel=\"icon\" href=\"/favicon.svg\" />");
        html.Append("</head><body>");

        html.Append("<header class=\"masthead\">");
        html.Append("<a class=\"brand\" href=\"");
        html.Append(crumb is null ? "/" : Attribute(crumb.Value.Href));
        html.Append("\">");
        html.Append(crumb is null ? Text(context.InstanceName) : Text(crumb.Value.Label));
        html.Append("</a>");
        html.Append("</header>");

        html.Append("<main>").Append(body).Append("</main>");

        html.Append("<footer><p>Published by its authors on ").Append(Text(context.InstanceName));
        html.Append(". The content of this page is not written or checked by ")
            .Append(Text(context.InstanceName)).Append(". ");
        // The address travels with the link, because the reader has no account, no session and no
        // script here: without it a report arrives saying "a wiki page" and a moderator has nothing
        // to act on.
        var report = reportRef is null
            ? context.SupportUrl
            : $"{context.SupportUrl}?wiki={Uri.EscapeDataString(reportRef)}";

        html.Append("<a href=\"").Append(Attribute(report))
            .Append("\" rel=\"noreferrer\">Report this page</a>.</p></footer>");

        html.Append("</body></html>");

        return html.ToString();
    }

    private static string Text(string value) => WebUtility.HtmlEncode(value);

    private static string Attribute(string value) => WebUtility.HtmlEncode(value);
}
