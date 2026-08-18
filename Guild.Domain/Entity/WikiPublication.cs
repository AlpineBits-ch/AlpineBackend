using System.Linq.Expressions;
using Guild.Domain.Enums;

namespace Guild.Domain.Entity;

/// <summary>
/// The one place that decides whether an anonymous reader may see a wiki page. Every public read
/// goes through here so that "what makes a page public" is a single expression with a test on it
/// rather than a predicate copied into each query.
/// </summary>
public static class WikiPublication
{
    /// <summary>
    /// The page half of the gate, as something EF can translate. Visibility appears here as a veto
    /// and never as a grant: it has always defaulted to Public meaning "visible to the guild", so a
    /// page that has not been opted in stays private however that column reads.
    /// </summary>
    public static Expression<Func<WikiPage, bool>> IsPageOptedIn { get; } =
        page => page.PublishedAt != null && page.Visibility == WikiVisibility.Public;

    private static readonly Func<WikiPage, bool> PageOptedIn = IsPageOptedIn.Compile();

    /// <summary>Whether an anonymous reader may see this page.</summary>
    /// <param name="wiki">The guild's wiki root, or null when it has none.</param>
    /// <param name="page">The page being asked for.</param>
    /// <returns>True only when the guild opted in, the page opted in, and the page is not private.</returns>
    public static bool IsPublic(Wiki? wiki, WikiPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return wiki?.PublishedSlug is not null && PageOptedIn(page);
    }

    /// <summary>
    /// Whether a page may be published at all. A page marked Private inside the guild cannot
    /// coherently be on the open internet; a page marked Public is still only visible to the guild
    /// until somebody sets <see cref="WikiPage.PublishedAt"/>.
    /// </summary>
    /// <param name="page">The page somebody is trying to publish.</param>
    /// <returns>True when nothing about the page itself refuses publication.</returns>
    public static bool MayBePublished(WikiPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Visibility == WikiVisibility.Public;
    }

    /// <summary>
    /// Whether a cover image lives somewhere this instance controls. An off-instance cover on a
    /// public page is a beacon that logs the address and user agent of every anonymous visitor to a
    /// host the page author picked.
    /// </summary>
    /// <param name="url">The cover URL, which may be null, app-relative or absolute.</param>
    /// <param name="allowedHosts">Hostnames this instance serves media from.</param>
    /// <returns>True when the URL is safe to serve on a public page.</returns>
    public static bool IsInstanceHosted(string? url, IReadOnlyCollection<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        if (string.IsNullOrWhiteSpace(url)) return true;

        var trimmed = url.Trim();

        // A protocol-relative //evil.example is not app-relative, however much it looks like it.
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith('/')) return true;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        return allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
