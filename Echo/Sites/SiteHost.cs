using AppEnvironment;

namespace Echo.Sites;

/// <summary>Works out which hostname a gateway-served site lives on.</summary>
public static class SiteHost
{
    /// <summary>The hostname for a site, from its label.</summary>
    public static string Resolve(string label, string environmentVariable) =>
        Normalise(Environment.GetEnvironmentVariable(environmentVariable))
        ?? DeriveFrom(label, Env.GeneralConfiguration.InstanceUrl);

    /// <summary>
    /// Reduces a configured value to the bare hostname the Host header will actually carry.
    /// </summary>
    public static string? Normalise(string? configured)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host.ToLowerInvariant()
                : null;
        }

        // Bare host, possibly with a port or a trailing slash.
        value = value.TrimEnd('/');

        var slash = value.IndexOf('/');
        if (slash > 0) value = value[..slash];

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out _)) value = value[..colon];

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>Turns the instance URL into the hostname a site lives on.</summary>
    public static string DeriveFrom(string label, string? instanceUrl) =>
        InstanceHosts.DeriveSibling(label, instanceUrl);

    /// <summary>
    /// Whether a request is for this site's label but not for the name it is bound to, which is the
    /// misconfiguration worth explaining rather than answering with an empty 404.
    /// </summary>
    /// <param name="requested">The hostname the request arrived on.</param>
    /// <param name="boundHost">The hostname this site is served on.</param>
    /// <param name="label">The site's label.</param>
    /// <returns>True when the request meant this site and will not reach it.</returns>
    public static bool IsMisdirected(string? requested, string boundHost, string label)
    {
        if (string.IsNullOrEmpty(requested)) return false;

        if (!requested.StartsWith($"{label}.", StringComparison.OrdinalIgnoreCase)) return false;
        if (requested.Equals(boundHost, StringComparison.OrdinalIgnoreCase)) return false;

        // Depth matters: a published wiki lives at admin.wiki.<instance>, one label deeper than the
        // console at admin.<instance>, and claiming that request would take the wiki's own hostname
        // away from it.
        return requested.Count(c => c == '.') == boundHost.Count(c => c == '.');
    }

    /// <summary>The scheme the instance is reached over.</summary>
    public static string Scheme =>
        Uri.TryCreate(Env.GeneralConfiguration.InstanceUrl, UriKind.Absolute, out var uri)
            ? uri.Scheme
            : Uri.UriSchemeHttps;

    /// <summary>
    /// The absolute base URL of a site, for links that leave the system - a ban notice pointing at
    /// the appeal form, a support email quoting the ticket URL.
    /// </summary>
    public static string BaseUrl(string host) => $"{Scheme}://{host}";
}
