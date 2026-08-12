using AppEnvironment;

namespace Import.Application.Endpoints;

/// <summary>Where a finished Discord import is allowed to put the browser.</summary>
public static class DiscordImportReturnTargets
{
    /// <summary>The desktop/mobile deep link. Stays the default so the existing flow is untouched.</summary>
    public static string Default => Env.DiscordImport.ClientReturnUrl;

    /// <summary>The browser client's progress page.</summary>
    public static string WebClient => WebClientHost.Link(WebClientHost.DiscordImportPath);

    public static IReadOnlyList<string> Allowed => [Default, WebClient];

    /// <summary>The target to use for <paramref name="requested"/>, falling back to
    /// <see cref="Default"/> for anything unrecognised.</summary>
    public static string Resolve(string? requested) =>
        !string.IsNullOrWhiteSpace(requested)
        && Allowed.Any(allowed => string.Equals(allowed, requested, StringComparison.OrdinalIgnoreCase))
            ? requested
            : Default;
}
