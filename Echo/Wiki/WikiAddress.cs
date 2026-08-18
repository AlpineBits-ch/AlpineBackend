namespace Echo.Wiki;

/// <summary>A published wiki, and optionally one page of it.</summary>
/// <param name="Slug">The wiki's published slug.</param>
/// <param name="PageSlug">One page's slug, or null for the wiki itself.</param>
public readonly record struct WikiAddress(string Slug, string? PageSlug)
{
    /// <summary>How the address reads in a console field or an audit line.</summary>
    public override string ToString() => PageSlug is null ? Slug : $"{Slug}/{PageSlug}";
}

/// <summary>
/// Reads whatever a moderator pasted as the address of a published wiki page.
///
/// A report names a page in whichever form the reporter had in front of them, and there are four:
/// the wiki's own hostname, the apex's path form that redirects to it, and both of those without a
/// scheme. Making a moderator normalise that by hand is how the wrong page gets taken down.
/// </summary>
public static class WikiAddresses
{
    /// <summary>Parses a pasted address.</summary>
    /// <param name="value">A slug, a slug and page, or any wiki URL.</param>
    /// <param name="apexHost">The hostname the wiki site is bound to.</param>
    /// <param name="address">The wiki and page it names.</param>
    /// <returns>True when it named a wiki.</returns>
    public static bool TryParse(string? value, string apexHost, out WikiAddress address)
    {
        address = default;

        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        // A bare host pastes without a scheme, and Uri needs one to see a host at all.
        if (!text.Contains("://", StringComparison.Ordinal)
            && text.Contains($".{apexHost}", StringComparison.OrdinalIgnoreCase))
        {
            text = $"https://{text}";
        }

        string? hostSlug = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var match = WikiHost.Match(uri.Host, apexHost);
            if (!match.IsWikiHost) return false;

            hostSlug = match.Slug;
            text = uri.AbsolutePath;
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Off a wiki's own hostname the path is the page and nothing else; off the apex, or off a
        // bare paste, the first segment is the wiki.
        var slug = hostSlug ?? (parts.Length > 0 ? parts[0] : null);
        if (slug is null) return false;

        var pageParts = hostSlug is null ? parts.Skip(1).ToArray() : parts;
        if (pageParts.Length > 1) return false;

        slug = Uri.UnescapeDataString(slug).ToLowerInvariant();
        if (!WikiHost.IsLabel(slug)) return false;

        var page = pageParts.Length == 1 ? Uri.UnescapeDataString(pageParts[0]) : null;
        if (page is { Length: 0 }) page = null;

        address = new WikiAddress(slug, page);
        return true;
    }
}
