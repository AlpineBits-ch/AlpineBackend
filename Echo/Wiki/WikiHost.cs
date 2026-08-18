namespace Echo.Wiki;

/// <summary>What a request's Host header names on the wiki site.</summary>
/// <param name="IsWikiHost">Whether the host belongs to the wiki site at all.</param>
/// <param name="Slug">The published wiki named by the leftmost label, or null on the apex.</param>
public readonly record struct WikiHostMatch(bool IsWikiHost, string? Slug);

/// <summary>
/// Splits <c>{slug}.wiki.&lt;instance&gt;</c> into the wiki it names, and decides which hostnames the
/// site answers on at all.
/// </summary>
public static class WikiHost
{
    /// <summary>The DNS limit on one label, which is now the ceiling on a published slug.</summary>
    public const int MaxSlugLength = 63;

    /// <summary>
    /// Never a wiki, whatever the guild service would say. A published slug cannot be <c>www</c> -
    /// the vanity grammar reserves it - but the label arrives from the network rather than from the
    /// database, and <c>www.wiki.&lt;instance&gt;</c> is the one somebody types by habit.
    /// </summary>
    private const string ReservedLabel = "www";

    /// <summary>Works out which wiki, if any, a request is for.</summary>
    /// <param name="requestHost">The Host header's hostname, without scheme or port.</param>
    /// <param name="apexHost">The hostname the site is bound to.</param>
    /// <returns>Whether this is the wiki site, and the wiki it names.</returns>
    public static WikiHostMatch Match(string? requestHost, string apexHost)
    {
        if (string.IsNullOrEmpty(requestHost) || string.IsNullOrEmpty(apexHost))
            return new WikiHostMatch(false, null);

        var host = requestHost.Trim().TrimEnd('.').ToLowerInvariant();
        var apex = apexHost.Trim().TrimEnd('.').ToLowerInvariant();

        if (host.Equals(apex, StringComparison.Ordinal)) return new WikiHostMatch(true, null);

        var suffix = $".{apex}";
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return new WikiHostMatch(false, null);

        var label = host[..^suffix.Length];

        // Only one label deep. Anything further down is a name nobody publishes on, and treating it
        // as the wiki would hand an unreachable address a rendered page.
        if (label.Contains('.') || !IsLabel(label) || label.Equals(ReservedLabel, StringComparison.Ordinal))
            return new WikiHostMatch(true, null);

        return new WikiHostMatch(true, label);
    }

    /// <summary>
    /// Whether a slug can be a hostname. Stricter than a URL path segment: an underscore is legal in
    /// a path and illegal in a label, and a label caps at 63 characters rather than at the slug
    /// column's width.
    /// </summary>
    /// <param name="value">The candidate label.</param>
    /// <returns>True when it is a valid DNS label.</returns>
    public static bool IsLabel(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSlugLength) return false;
        if (value[0] == '-' || value[^1] == '-') return false;

        foreach (var c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') continue;
            return false;
        }

        return true;
    }

    /// <summary>The hostname one published wiki is canonically served from.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <param name="apexHost">The hostname the site is bound to.</param>
    /// <returns>The wiki's own hostname.</returns>
    public static string HostFor(string slug, string apexHost) => $"{slug}.{apexHost}";
}
