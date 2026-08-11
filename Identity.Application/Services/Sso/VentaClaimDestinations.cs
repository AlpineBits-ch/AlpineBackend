using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Identity.Application.Services.Sso;

/// <summary>Decides which token each claim is allowed into.</summary>
public static class VentaClaimDestinations
{
    /// <summary>Claim carrying the <see cref="Domain.Entities.LoginSession"/> this token belongs to.</summary>
    public const string SessionId = "session_id";

    /// <summary>Set on bot accounts so downstream services can tag authorship without a lookup.</summary>
    public const string UserType = "user_type";

    /// <summary>Never leaves Identity, in either token.</summary>
    private const string SecurityStamp = "AspNet.Identity.SecurityStamp";

    /// <summary>
    /// Applies the destination policy to every claim on <paramref name="principal"/>.
    /// </summary>
    public static void Apply(ClaimsPrincipal principal)
    {
        var scopes = principal.GetScopes();
        var hasEmail = scopes.Contains(OpenIddictConstants.Scopes.Email);
        var hasProfile = scopes.Contains(OpenIddictConstants.Scopes.Profile);
        var hasRoles = scopes.Contains(OpenIddictConstants.Scopes.Roles);

        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(DestinationsFor(claim, hasEmail, hasProfile, hasRoles));
        }
    }

    private static IEnumerable<string> DestinationsFor(Claim claim, bool hasEmail, bool hasProfile, bool hasRoles)
    {
        switch (claim.Type)
        {
            // Who the token is about.
            case OpenIddictConstants.Claims.Subject:
                return [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken];

            case SecurityStamp:
                return [];

            // How and when the person authenticated.
            case OpenIddictConstants.Claims.AuthenticationTime:
            case OpenIddictConstants.Claims.AuthenticationMethodReference:
                return [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken];

            // The session this token belongs to, which is how refresh enforces revocation.
            case SessionId:
            case UserType:
                return [OpenIddictConstants.Destinations.AccessToken];

            case OpenIddictConstants.Claims.Email:
            case OpenIddictConstants.Claims.EmailVerified:
                return hasEmail
                    ? [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken]
                    : [OpenIddictConstants.Destinations.AccessToken];

            case OpenIddictConstants.Claims.Name:
            case OpenIddictConstants.Claims.PreferredUsername:
            case OpenIddictConstants.Claims.Picture:
                return hasProfile
                    ? [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken]
                    : [OpenIddictConstants.Destinations.AccessToken];

            case OpenIddictConstants.Claims.Role:
                return hasRoles
                    ? [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken]
                    : [OpenIddictConstants.Destinations.AccessToken];

            // Everything else: the access token only.
            default:
                return [OpenIddictConstants.Destinations.AccessToken];
        }
    }
}
