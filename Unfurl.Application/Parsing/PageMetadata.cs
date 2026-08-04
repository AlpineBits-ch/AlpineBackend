namespace Unfurl.Application.Parsing;

/// <summary>
/// What a page said about itself, before any of it is trusted enough to become an embed.
///
/// <para>Notice what is <b>not</b> here: any notion of a player, an iframe or embeddable HTML. That
/// omission is structural, not an oversight. Generic parsing must never be able to produce a
/// <c>video.url</c>, because a client renders that in an iframe - so the type a generic parse
/// returns simply cannot express one. Only <c>ProviderRegistry</c>, working from a whitelist, can.
/// See the architecture test in Unfurl.Tests.</para>
/// </summary>
public sealed class PageMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? CanonicalUrl { get; set; }
    public string? SiteName { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorUrl { get; set; }

    /// <summary>Absolute URL of the page's own preview image, if it offered one.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Whether the page asked for its image to be shown large (og:type article / summary
    /// _large_image) rather than as a small thumbnail beside the text.</summary>
    public bool PrefersLargeImage { get; set; }

    /// <summary>Raw <c>og:type</c>, used to pick an embed type.</summary>
    public string? OpenGraphType { get; set; }

    public string? ThemeColor { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
