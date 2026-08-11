namespace AppEnvironment;

/// <summary>
/// Works out the hostname of a site that sits beside the API rather than under it.
/// </summary>
public static class InstanceHosts
{
    /// <summary>Turns an instance URL into the hostname a sibling site lives on.</summary>
    public static string DeriveSibling(string label, string? instanceUrl)
    {
        // A misconfigured instance URL must not take a service down at startup.
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

    /// <summary>The absolute base URL of a sibling site.</summary>
    public static string DeriveSiblingUrl(string label, string? instanceUrl)
    {
        var scheme = Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri)
            ? uri.Scheme
            : Uri.UriSchemeHttps;

        return $"{scheme}://{DeriveSibling(label, instanceUrl)}";
    }
}
