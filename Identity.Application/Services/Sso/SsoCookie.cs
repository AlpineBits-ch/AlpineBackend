using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenIddict.Abstractions;

namespace Identity.Application.Services.Sso;

/// <summary>
/// The browser session at the identity provider - the thing that makes single sign-on single.
/// </summary>
public static class SsoCookie
{
    /// <summary>Authentication scheme name. Never the default scheme - always opted into explicitly.</summary>
    public const string Scheme = "VentaSso";

    public const string CookieName = "__Host-venta_sso";

    /// <summary>How long the cookie survives without use. Renewed on activity.</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);

    /// <summary>The hard cap.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    /// <summary>Builds the ticket.</summary>
    public static ClaimsPrincipal BuildPrincipal(
        string subject, string sessionId, DateTimeOffset authenticatedAt, IEnumerable<string> methods)
    {
        var identity = new ClaimsIdentity(Scheme, OpenIddictConstants.Claims.Name, ClaimTypes.Role);

        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, subject));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        identity.AddClaim(new Claim(VentaClaimDestinations.SessionId, sessionId));
        identity.AddClaim(new Claim(
            OpenIddictConstants.Claims.AuthenticationTime,
            authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64));

        foreach (var method in methods.Distinct(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, method));
        }

        return new ClaimsPrincipal(identity);
    }

    public static string? Subject(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(OpenIddictConstants.Claims.Subject)
        ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? SessionId(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(VentaClaimDestinations.SessionId);

    public static string[] Methods(ClaimsPrincipal? principal) =>
        principal?.FindAll(OpenIddictConstants.Claims.AuthenticationMethodReference)
            .Select(claim => claim.Value).ToArray() ?? [];

    public static DateTimeOffset? AuthenticatedAt(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirstValue(OpenIddictConstants.Claims.AuthenticationTime);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>Cookie-scheme validation hook.</summary>
    public static async Task EnforceAbsoluteLifetimeAsync(CookieValidatePrincipalContext context)
    {
        var authenticatedAt = AuthenticatedAt(context.Principal);

        if (authenticatedAt is null || DateTimeOffset.UtcNow - authenticatedAt > AbsoluteLifetime)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
    }
}
