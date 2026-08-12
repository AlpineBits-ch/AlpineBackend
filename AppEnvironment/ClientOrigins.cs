namespace AppEnvironment;

/// <summary>
/// The browser origins allowed to read authenticated API responses - the origin half of
/// <c>AlpinePolicy</c>.
/// </summary>
public static class ClientOrigins
{
    /// <summary>Extra origins, separated by commas, semicolons or whitespace.</summary>
    public const string EnvironmentVariable = "CORS_ALLOWED_ORIGINS";

    /// <summary>The label the web client's host is derived under, per <see cref="InstanceHosts"/>.
    /// Restated from <see cref="WebClientHost.Label"/> so the two cannot drift.</summary>
    public const string WebClientLabel = WebClientHost.Label;

    /// <summary>Never allowed, and the check is not decoration.</summary>
    public const string AnyOrigin = "*";

    /// <summary>
    /// Origins that are part of shipping the product rather than part of a deployment.
    /// </summary>
    private static readonly string[] Builtin =
    [
        "http://localhost:1420",
        "http://localhost:4200",
        "https://chat.alpinebits.ch",
        "http://tauri.localhost",
        "tauri://localhost",
    ];

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>The allowlist for this process.</summary>
    public static IReadOnlyList<string> Allowed => Resolve(
        Env.GeneralConfiguration.InstanceUrl,
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        WebClientHost.BaseUrl);

    /// <summary>Configured entries that were thrown away, so startup can say so out loud.</summary>
    public static IReadOnlyList<string> Rejected =>
        Rejects(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// The built-ins, plus the web client derived from <paramref name="instanceUrl"/>, plus whatever
    /// <paramref name="configured"/> adds - normalised, de-duplicated, and with anything unusable
    /// dropped.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? instanceUrl, string? configured) =>
        Resolve(instanceUrl, configured, InstanceHosts.DeriveSiblingUrl(WebClientLabel, instanceUrl));

    /// <summary>
    /// As <see cref="Resolve(string?,string?)"/>, with the web origin supplied rather than derived.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? instanceUrl, string? configured, string? webClientOrigin)
    {
        var origins = new List<string>();

        foreach (var candidate in Builtin
                     .Append(webClientOrigin ?? string.Empty)
                     .Concat(Split(configured)))
        {
            var normalised = Normalise(candidate);
            if (normalised is null) continue;
            // Ordinal: these are compared against the Origin header with StringComparer.Ordinal by
            // CorsService, so two entries differing only in case are genuinely two entries - and
            // Normalise has already lower-cased both.
            if (!origins.Contains(normalised, StringComparer.Ordinal)) origins.Add(normalised);
        }

        return origins;
    }

    /// <summary>The configured entries <see cref="Resolve"/> refused, verbatim.</summary>
    public static IReadOnlyList<string> Rejects(string? configured) =>
        Split(configured).Where(entry => Normalise(entry) is null).ToList();

    private static IEnumerable<string> Split(string? configured) =>
        configured?.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];

    /// <summary>An origin as a browser sends it, or null when the value cannot be one.</summary>
    private static string? Normalise(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var trimmed = candidate.Trim();

        // Belt and braces, and knowingly so: Uri.TryCreate already refuses "*", "https://*" and
        // "https://*.venta.gg" outright, so no test can distinguish this line being here from it
        // being absent.
        if (trimmed.Contains(AnyOrigin, StringComparison.Ordinal)) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (string.IsNullOrEmpty(uri.Scheme) || string.IsNullOrEmpty(uri.Host)) return null;

        // A path, query or fragment means somebody pasted a URL rather than named an origin.
        if (uri.AbsolutePath is not ("" or "/")) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        // No lower-casing here on purpose: Uri canonicalises both scheme and host to lower case
        // already, for custom schemes as well as http/https (verified - "TAURI://LocalHost" parses
        // to scheme "tauri", host "localhost").
        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
