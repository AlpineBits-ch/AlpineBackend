using System.Globalization;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Unfurl.Application.Parsing;

/// <summary>
/// Reads a page's self-description out of its HTML.
///
/// <para>AngleSharp rather than regex over <c>&lt;meta&gt;</c>: real-world markup is full of
/// unquoted attributes, stray <c>&lt;</c>, mis-nested tags and content that merely looks like a
/// tag. A regex over that either misses the tags or matches text inside a comment, and getting it
/// wrong here means rendering an attacker's string as a page title.</para>
///
/// <para>Precedence per field is Open Graph → Twitter Card → bare HTML → nothing, which is the
/// order of how deliberately the author chose the value. Everything extracted is treated as hostile
/// text: HTML-decoded by the parser, then stripped of control characters before it leaves.</para>
/// </summary>
public class MetadataParser
{
    private static readonly HtmlParser Parser = new();

    public async Task<PageMetadata> ParseAsync(byte[] html, Uri baseUrl, CancellationToken ct)
    {
        using var stream = new MemoryStream(html);
        using var document = await Parser.ParseDocumentAsync(stream, ct);

        var meta = new PageMetadata
        {
            Title = Clean(
                Property(document, "og:title")
                ?? Name(document, "twitter:title")
                ?? document.Title),

            Description = Clean(
                Property(document, "og:description")
                ?? Name(document, "twitter:description")
                ?? Name(document, "description")),

            SiteName = Clean(Property(document, "og:site_name")) ?? RegistrableName(baseUrl),

            OpenGraphType = Clean(Property(document, "og:type")),

            ThemeColor = Clean(Name(document, "theme-color")),

            AuthorName = Clean(
                Property(document, "article:author")
                ?? Name(document, "author")),
        };

        // og:url is the page's claim about its own canonical address. Kept only when it points at
        // the same host we actually fetched: a page is free to claim any URL it likes, and a card
        // whose title links somewhere other than where the content came from is a phishing
        // primitive, not a feature.
        var canonical = Property(document, "og:url") ?? Rel(document, "canonical");
        if (Uri.TryCreate(canonical, UriKind.Absolute, out var canonicalUri)
            && string.Equals(canonicalUri.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            meta.CanonicalUrl = canonicalUri.AbsoluteUri;
        }

        var image =
            Property(document, "og:image:secure_url")
            ?? Property(document, "og:image")
            ?? Name(document, "twitter:image")
            ?? Name(document, "twitter:image:src");

        if (Uri.TryCreate(baseUrl, image, out var imageUri)
            && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
        {
            meta.ImageUrl = imageUri.AbsoluteUri;
        }

        // summary_large_image is Twitter's explicit "render this big"; og:type article implies the
        // same in practice. Anything else gets the small thumbnail treatment.
        var twitterCard = Name(document, "twitter:card");
        meta.PrefersLargeImage =
            string.Equals(twitterCard, "summary_large_image", StringComparison.OrdinalIgnoreCase)
            || (meta.OpenGraphType?.StartsWith("article", StringComparison.OrdinalIgnoreCase) ?? false);

        var published = Property(document, "article:published_time") ?? Property(document, "og:updated_time");
        if (DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var publishedAt))
        {
            meta.PublishedAt = publishedAt;
        }

        var authorUrl = Property(document, "article:author");
        if (Uri.TryCreate(authorUrl, UriKind.Absolute, out var authorUri)
            && (authorUri.Scheme == Uri.UriSchemeHttp || authorUri.Scheme == Uri.UriSchemeHttps))
        {
            meta.AuthorUrl = authorUri.AbsoluteUri;
            // article:author holding a URL means it was never a display name; drop the duplicate.
            if (meta.AuthorName == authorUri.AbsoluteUri) meta.AuthorName = null;
        }

        return meta;
    }

    private static string? Property(IHtmlDocument document, string property) =>
        document.QuerySelector($"meta[property='{Escape(property)}']")?.GetAttribute("content");

    private static string? Name(IHtmlDocument document, string name) =>
        document.QuerySelector($"meta[name='{Escape(name)}']")?.GetAttribute("content");

    private static string? Rel(IHtmlDocument document, string rel) =>
        document.QuerySelector($"link[rel='{Escape(rel)}']")?.GetAttribute("href");

    private static string Escape(string value) => value.Replace("'", "\\'");

    /// <summary>
    /// Collapses whitespace and removes control characters.
    ///
    /// <para>Control characters are the point. A title containing U+202E (right-to-left override)
    /// reverses the rendering of everything after it in most clients, which is the classic trick
    /// for making <c>gpj.exe</c> read as <c>exe.jpg</c>; newlines let a one-line card become twenty.
    /// Neither can ever be legitimate in a page title.</para>
    /// </summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Control characters become a space rather than vanishing, then runs of whitespace collapse.
        // Deleting them outright welds words together - a title written across two lines came back
        // as "onetwothree" - which is worse than the newline it was trying to remove.
        //
        // Bidi overrides are the exception and are deleted: they are not whitespace, they are a
        // rendering instruction, and substituting a space for one would leave a gap where an
        // invisible character used to be.
        var chars = value
            .Where(c => !IsBidiOverride(c))
            .Select(c => char.IsControl(c) ? ' ' : c)
            .ToArray();

        var collapsed = string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
    }

    private static bool IsBidiOverride(char c) =>
        c is '‪' or '‫' or '‬' or '‭' or '‮'
            or '⁦' or '⁧' or '⁨' or '⁩' or '‏' or '‎';

    /// <summary>Host without a leading "www.", used as the provider name when a page did not give
    /// one. Not a full public-suffix parse - it only ever becomes display text.</summary>
    private static string RegistrableName(Uri url) =>
        url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
}
