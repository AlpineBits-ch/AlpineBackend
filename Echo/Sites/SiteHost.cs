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
    public static string DeriveFrom(string label, string? instanceUrl)
    {
        // A misconfigured InstanceUrl must not take the gateway down at startup.
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri)) return $"{label}.localhost";

        var host = uri.Host;
        if (host.StartsWith($"{label}.", StringComparison.OrdinalIgnoreCase)) return host;

        var labels = host.Split('.');

        // A bare registrable domain (venta.gg) or a single label (localhost) gets a prefix; an
        // address gets one too, because there is nothing sensible to derive from it.
        if (labels.Length < 3 || System.Net.IPAddress.TryParse(host, out _)) return $"{label}.{host}";

        // Already a subdomain: replace its first label.
        return string.Join('.', labels.Skip(1).Prepend(label));
    }

    /// <summary>
    /// The absolute base URL of a site, for links that leave the system - a ban notice pointing at
    /// the appeal form, a support email quoting the ticket URL.
    /// </summary>
    public static string BaseUrl(string host)
    {
        var scheme = Uri.TryCreate(Env.GeneralConfiguration.InstanceUrl, UriKind.Absolute, out var uri)
            ? uri.Scheme
            : Uri.UriSchemeHttps;

        return $"{scheme}://{host}";
    }
}
