using OpenIddict.Abstractions;

namespace Identity.Application.Services.Sso;

/// <summary>What a scope is called, and what it means, in words a person can act on.</summary>
public sealed record SsoScopeDescription(string Name, string Title, string Description);

/// <summary>The consent screen's vocabulary.</summary>
public static class SsoScopeCatalog
{
    private static readonly Dictionary<string, SsoScopeDescription> Known = new(StringComparer.Ordinal)
    {
        [OpenIddictConstants.Scopes.OpenId] = new(
            OpenIddictConstants.Scopes.OpenId,
            "Confirm who you are",
            "Sees that you are signed in, and the account identifier that stands for you."),

        [OpenIddictConstants.Scopes.Profile] = new(
            OpenIddictConstants.Scopes.Profile,
            "Your profile",
            "Your username, display name and avatar."),

        [OpenIddictConstants.Scopes.Email] = new(
            OpenIddictConstants.Scopes.Email,
            "Your email address",
            "The address on your account, and whether it has been verified."),

        [OpenIddictConstants.Scopes.Roles] = new(
            OpenIddictConstants.Scopes.Roles,
            "Your roles",
            "The roles your account holds, so the site can tell what you are allowed to do there."),

        [OpenIddictConstants.Scopes.OfflineAccess] = new(
            OpenIddictConstants.Scopes.OfflineAccess,
            "Stay signed in",
            "Keeps you signed in to this site without asking again each time you return."),
    };

    public static SsoScopeDescription Describe(string scope) =>
        Known.TryGetValue(scope, out var description)
            ? description
            : new SsoScopeDescription(scope, scope, string.Empty);

    /// <summary>The scopes worth showing.</summary>
    public static IReadOnlyList<SsoScopeDescription> Describe(IEnumerable<string> requested) =>
        [.. requested
            .Where(scope => scope != OpenIddictConstants.Scopes.OpenId)
            .Distinct(StringComparer.Ordinal)
            .Select(Describe)];
}
