using AppEnvironment;

namespace Identity.Application.Services.Steam;

/// <summary>Where a finished Steam flow is allowed to put the browser.</summary>
public static class SteamReturnTargets
{
    /// <summary>The page on the auth site that reads <c>?status=</c> and takes it from there.</summary>
    public const string AuthSitePath = "/steam";

    /// <summary>The mobile deep link, which stays the default so the existing flow is untouched.</summary>
    public static string Default => Env.Steam.ClientReturnUrl;

    public static string AuthSite => Env.AuthConfiguration.IssuerUrl.TrimEnd('/') + AuthSitePath;

    /// <summary>
    /// The browser client's page, for a flow started from a tab rather than from the desktop app.
    /// </summary>
    public static string WebClient => WebClientHost.Link(WebClientHost.SteamAuthPath);

    public static IReadOnlyList<string> Allowed => [Default, AuthSite, WebClient];

    /// <summary>The target to use for <paramref name="requested"/>.</summary>
    public static string Resolve(string? requested) =>
        !string.IsNullOrWhiteSpace(requested)
        && Allowed.Any(allowed => string.Equals(allowed, requested, StringComparison.OrdinalIgnoreCase))
            ? requested
            : Default;
}
