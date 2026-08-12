namespace AppEnvironment;

/// <summary>
/// Where the browser client is served, and the https equivalents of the <c>venta://</c> deep links.
/// </summary>
public static class WebClientHost
{
    /// <summary>The label the host is derived under.</summary>
    public const string Label = "app";

    /// <summary>Explicit override for a deployment that does not follow the sibling convention. Named
    /// like the other site variables (<c>ADMIN_DOMAIN</c>, <c>AUTH_DOMAIN</c>).</summary>
    public const string EnvironmentVariable = "APP_DOMAIN";

    /// <summary>Path of the Steam link/login return page. Matches <c>venta://steam-auth</c>.</summary>
    public const string SteamAuthPath = "/steam-auth";

    /// <summary>Path of the Discord-import progress page. Matches <c>venta://discord-import</c>.</summary>
    public const string DiscordImportPath = "/discord-import";

    /// <summary>Path of the bot install prompt. Matches <c>venta://install-bot</c>.</summary>
    public const string InstallBotPath = "/install-bot";

    /// <summary>Path prefix of an invite. Matches <c>venta://invite/{code}</c>.</summary>
    public const string InvitePath = "/invite";

    /// <summary>Absolute base URL of the web client, with no trailing slash.</summary>
    public static string BaseUrl =>
        Normalise(Environment.GetEnvironmentVariable(EnvironmentVariable))
        ?? InstanceHosts.DeriveSiblingUrl(Label, Env.GeneralConfiguration.InstanceUrl).TrimEnd('/');

    /// <summary>An absolute web-client URL for one of the paths above.</summary>
    public static string Link(string path) => BaseUrl + path;

    /// <summary>
    /// Accepts a bare hostname or a full URL for <c>APP_DOMAIN</c>, and returns an absolute base
    /// URL.
    /// </summary>
    private static string? Normalise(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var value = configured.Trim().TrimEnd('/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        // A bare hostname.
        var scheme = Uri.TryCreate(Env.GeneralConfiguration.InstanceUrl, UriKind.Absolute, out var instance)
            ? instance.Scheme
            : Uri.UriSchemeHttps;

        return $"{scheme}://{value}";
    }
}
