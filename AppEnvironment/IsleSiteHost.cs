namespace AppEnvironment;

/// <summary>Where the Isle companion website is served.</summary>
public static class IsleSiteHost
{
    /// <summary>The label the host is derived under.</summary>
    public const string Label = "isle";

    /// <summary>Explicit override, named like <c>ADMIN_DOMAIN</c> and <c>APP_DOMAIN</c>.</summary>
    public const string EnvironmentVariable = "ISLE_DOMAIN";

    /// <summary>Path of the Steam link return page.</summary>
    public const string SteamAuthPath = "/steam";

    /// <summary>Absolute base URL of the isle site, with no trailing slash.</summary>
    public static string BaseUrl =>
        Normalise(Environment.GetEnvironmentVariable(EnvironmentVariable))
        ?? InstanceHosts.DeriveSiblingUrl(Label, Env.GeneralConfiguration.InstanceUrl).TrimEnd('/');

    /// <summary>An absolute isle-site URL for one of the paths above.</summary>
    public static string Link(string path) => BaseUrl + path;

    /// <summary>Accepts a bare hostname or a full URL for <c>ISLE_DOMAIN</c>.</summary>
    private static string? Normalise(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var value = configured.Trim().TrimEnd('/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.GetLeftPart(UriPartial.Authority);

        var scheme = Uri.TryCreate(Env.GeneralConfiguration.InstanceUrl, UriKind.Absolute, out var instance)
            ? instance.Scheme
            : Uri.UriSchemeHttps;

        return $"{scheme}://{value}";
    }
}
