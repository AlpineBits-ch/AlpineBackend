namespace Identity.Application.Services.Sso;

/// <summary>
/// Turns a User-Agent header into something a person recognises in their session list.
/// </summary>
public static class BrowserLabel
{
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Browser";

        var browser = Browser(userAgent);
        var platform = Platform(userAgent);

        return platform is null ? browser : $"{browser} on {platform}";
    }

    private static string Browser(string ua) =>
        // Order matters and is the whole trick: every one of these also claims to be the ones below
        // it.
        ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
        : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera"
        : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
        : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
        : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
        : "Browser";

    private static string? Platform(string ua) =>
        ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
        : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
        : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
        : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
        : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
        : ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase) ? "ChromeOS"
        : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
        : null;
}
