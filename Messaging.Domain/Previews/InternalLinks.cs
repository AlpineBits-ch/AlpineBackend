namespace Messaging.Domain.Previews;

/// <summary>What an instance-local link points at. One value per card layout a client renders.</summary>
public enum InternalLinkKind
{
    /// <summary><c>/invite/{code}</c> - a guild invite.</summary>
    Invite,

    /// <summary><c>/wiki/{guildId}/{pageId}</c> - a page in a guild's wiki.</summary>
    WikiPage,
}

/// <summary>A link to something on this instance, with the route values pulled out of it.</summary>
public sealed class InternalLink
{
    public required InternalLinkKind Kind { get; init; }

    /// <summary>The normalized URL as <see cref="LinkExtractor"/> produced it, so the embed's
    /// <c>url</c> is the link the reader actually sees in the message.</summary>
    public required string Url { get; init; }

    /// <summary>Captured route values, keyed by the placeholder name in the template.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    public string Value(string key) => Values.TryGetValue(key, out var v) ? v : "";
}

/// <summary>
/// Recognizes this instance's own links so they are resolved in-process instead of fetched.
/// </summary>
public static class InternalLinkRecognizer
{
    /// <summary>The link shapes, as segment templates.</summary>
    private static readonly (InternalLinkKind Kind, string[] Template)[] Routes =
    [
        (InternalLinkKind.Invite, ["invite", "{code}"]),
        (InternalLinkKind.WikiPage, ["wiki", "{guildId}", "{pageId}"]),
    ];

    /// <summary>
    /// Whether this URL belongs to the instance at all, regardless of whether its shape is one we
    /// know.
    /// </summary>
    public static bool IsInternal(string? url, IReadOnlyCollection<string> instanceHosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsInternal(uri, instanceHosts);

    /// <summary>
    /// <paramref name="instanceHosts"/> is <c>AppEnvironment.InstanceLinkHosts.All</c>: bare
    /// hostnames where the deployment's URL used the scheme's default port, <c>host:port</c> where
    /// it named one.
    /// </summary>
    public static bool IsInternal(Uri uri, IReadOnlyCollection<string> instanceHosts) =>
        instanceHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
        || instanceHosts.Contains($"{uri.Host}:{uri.Port}", StringComparer.OrdinalIgnoreCase);

    /// <summary>Matches an internal URL against the route table.</summary>
    public static bool TryRecognize(
        string? url,
        IReadOnlyCollection<string> instanceHosts,
        out InternalLink? link)
    {
        link = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!IsInternal(uri, instanceHosts)) return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        foreach (var (kind, template) in Routes)
        {
            if (!TryMatch(template, segments, out var values)) continue;

            link = new InternalLink { Kind = kind, Url = uri.AbsoluteUri, Values = values! };
            return true;
        }

        return false;
    }

    private static bool TryMatch(string[] template, string[] segments, out Dictionary<string, string>? values)
    {
        values = null;
        if (segments.Length != template.Length) return false;

        var captured = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < template.Length; i++)
        {
            var part = template[i];

            if (part.StartsWith('{') && part.EndsWith('}'))
            {
                if (!IsPlausibleValue(segments[i])) return false;
                captured[part[1..^1]] = segments[i];
                continue;
            }

            if (!string.Equals(part, segments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        values = captured;
        return true;
    }

    /// <summary>
    /// The alphabet every id and code in this product is drawn from: prefixed ULIDs (<c>gild_…</c>,
    /// <c>wkpg_…</c>), generated invite codes, and the lowercase-hyphenated vanity codes that may
    /// stand in for them.
    /// </summary>
    private static bool IsPlausibleValue(string value)
    {
        if (value.Length is 0 or > 64) return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return false;
        }

        return true;
    }
}
