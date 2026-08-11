using System.Security.Claims;
using Identity.Application.Services.Sso;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Application.Controllers;

/// <summary>
/// The browser-facing half of the identity provider: <c>/connect/authorize</c>,
/// <c>/connect/userinfo</c> and <c>/connect/logout</c>.
/// </summary>
[ApiController]
public class AuthorizationController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictScopeManager scopes,
    MicroserviceContext ctx,
    AuthorizationRequestStash stash,
    ILogger<AuthorizationController> logger) : ControllerBase
{
    /// <summary>Where the static site's sign-in screen lives.</summary>
    private const string LoginPage = "/login";

    private const string ConsentPage = "/consent";

    private const string LogoutPage = "/logout";

    // ── /connect/authorize ──────────────────────────────────────────────────

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        // OpenIddict has already validated client_id, redirect_uri, response_type, the scopes and
        // the PKCE challenge.
        var request = HttpContext.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // A resumed pass carries the id of the request that was parked when we sent the browser to
        // the login page. Matches() is what stops that id being lifted onto a different request.
        var stashId = (string?)request.GetParameter(AuthorizationRequestStash.Parameter);
        var stashed = await stash.PeekAsync(stashId, ct);
        if (stashed is not null && (stashed.Kind != AuthorizationRequestStash.AuthorizeKind
                                    || !AuthorizationRequestStash.Matches(stashed, request)))
        {
            logger.LogWarning("Discarding stash {StashId}: it does not describe this authorization request", stashId);
            stashed = null;
            stashId = null;
        }

        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        var user = await ResolveSignedInUserAsync(cookie, ct);

        var reprompt = RepromptReason(request, cookie, user, stashed);
        if (reprompt is not null)
        {
            // A cookie that no longer resolves to an account that may sign in is not left lying
            // around to be re-evaluated on the next request.
            if (cookie.Succeeded && user is null) await HttpContext.SignOutAsync(SsoCookie.Scheme);

            logger.LogInformation("Authorization for client {ClientId} needs interactive sign-in: {Reason}",
                request.ClientId, reprompt);

            if (request.HasPromptValue(PromptValues.None)) return ProtocolError(Errors.LoginRequired,
                "The user is not signed in and prompt=none was requested.");

            return await ParkAndRedirectAsync(LoginPage, request, stashId, stashed, ct,
                markReprompted: true);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId!, ct)
                          ?? throw new InvalidOperationException("The client application cannot be found.");

        var clientId = await applications.GetIdAsync(application, ct);
        var requestedScopes = request.GetScopes();

        var existing = await FindAuthorizationsAsync(user!.Id, clientId!, requestedScopes, ct);

        var consent = await ConsentDecisionAsync(application, request, stashed, user, existing, ct);
        if (consent == ConsentOutcome.Denied)
        {
            if (stashId is not null) await stash.ConsumeAsync(stashId, ct);
            return ProtocolError(Errors.AccessDenied, "The user denied the authorization request.");
        }

        if (consent == ConsentOutcome.Required)
        {
            if (request.HasPromptValue(PromptValues.None)) return ProtocolError(Errors.ConsentRequired,
                "Consent is required and prompt=none was requested.");

            return await ParkAndRedirectAsync(ConsentPage, request, stashId, stashed, ct,
                markReprompted: false);
        }

        // From here the request completes. The stash has done its job.
        if (stashId is not null) await stash.ConsumeAsync(stashId, ct);

        var session = await CreateClientSessionAsync(user, application, ct);
        await ctx.SaveChangesAsync(ct);

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        principal.SetClaim(Claims.Subject, user.Id);
        principal.SetClaim(Claims.Email, user.Email);
        principal.SetClaim(VentaClaimDestinations.SessionId, session.Id);
        principal.SetScopes(requestedScopes);

        var resources = new List<string>();
        await foreach (var resource in scopes.ListResourcesAsync(principal.GetScopes(), ct))
        {
            resources.Add(resource);
        }
        principal.SetResources(resources);

        // Carried from the browser session rather than re-asserted: this is how the person actually
        // authenticated, and it is what makes a later max_age or prompt=login mean something.
        CarryAuthenticationContext(principal, SsoCookie.AuthenticatedAt(cookie.Principal),
            SsoCookie.Methods(cookie.Principal));

        var authorization = existing.LastOrDefault() ?? await authorizations.CreateAsync(
            principal, user.Id, clientId!, AuthorizationTypes.Permanent, principal.GetScopes(), ct);

        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization, ct));

        VentaClaimDestinations.Apply(principal);

        logger.LogInformation("Authorized client {ClientId} for user {UserId} on session {SessionId}",
            request.ClientId, user.Id, session.Id);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Expands a parked request back into a full authorization request and bounces the browser at
    /// it.
    /// </summary>
    [HttpGet("~/connect/authorize/resume")]
    [AllowAnonymous]
    public async Task<IActionResult> ResumeAuthorize([FromQuery] string rq, CancellationToken ct)
    {
        var stashed = await stash.PeekAsync(rq, ct);
        if (stashed is null || stashed.Kind != AuthorizationRequestStash.AuthorizeKind) return Expired();

        return Redirect("/connect/authorize" + AuthorizationRequestStash.QueryString(rq, stashed));
    }

    // ── /connect/logout ─────────────────────────────────────────────────────

    /// <summary>RP-initiated logout.</summary>
    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var stashId = (string?)request.GetParameter(AuthorizationRequestStash.Parameter);
        var stashed = await stash.PeekAsync(stashId, ct);

        if (stashed is { Kind: AuthorizationRequestStash.LogoutKind, Confirmed: true })
        {
            await stash.ConsumeAsync(stashId, ct);
            return await SignOutBrowserAsync(ct);
        }

        // OpenIddict surfaces a validated id_token_hint as the principal of its own scheme - but it
        // reports success on this endpoint whether or not a hint was supplied, so the subject is
        // what actually distinguishes "this client proved the person was signed in to it" from
        // "somebody sent a bare GET".
        var hint = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (hint.Principal?.GetClaim(Claims.Subject) is not null) return await SignOutBrowserAsync(ct);

        // Nothing to confirm: no browser session exists, so there is no sign-out to be tricked into
        // performing.
        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        if (!cookie.Succeeded) return await SignOutBrowserAsync(ct);

        var id = stashId is not null && stashed is not null
            ? stashId
            : await stash.StashAsync(AuthorizationRequestStash.LogoutKind, request, ct);

        return Redirect($"{LogoutPage}?{AuthorizationRequestStash.Parameter}={Uri.EscapeDataString(id)}");
    }

    [HttpGet("~/connect/logout/resume")]
    [AllowAnonymous]
    public async Task<IActionResult> ResumeLogout([FromQuery] string rq, CancellationToken ct)
    {
        var stashed = await stash.PeekAsync(rq, ct);
        if (stashed is null || stashed.Kind != AuthorizationRequestStash.LogoutKind) return Expired();

        return Redirect("/connect/logout" + AuthorizationRequestStash.QueryString(rq, stashed));
    }

    // ── /connect/userinfo ───────────────────────────────────────────────────

    /// <summary>
    /// The claims a relying party is allowed to read about the signed-in person, gated on the
    /// scopes it was granted.
    /// </summary>
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [IgnoreAntiforgeryToken]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        var subject = User.GetClaim(Claims.Subject) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (subject is null) return Unauthorized();

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsSigninAllowed())
        {
            // The token is well-formed but the account behind it is gone or barred. invalid_token
            // is what a conforming client acts on by discarding the token, rather than by retrying
            // with it.
            return Forbid(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme],
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictValidationAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription] =
                        "The account this token refers to can no longer sign in.",
                }));
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id,
        };

        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.PreferredUsername] = user.UserName ?? string.Empty;
            claims[Claims.Name] = user.UserName ?? string.Empty;
            claims[Claims.UpdatedAt] = user.UpdatedAt.ToUnixTimeSeconds();
        }

        if (User.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email ?? string.Empty;
            claims[Claims.EmailVerified] = user.EmailVerifiedAt is not null;
        }

        if (User.HasScope(Scopes.Roles))
        {
            claims[Claims.Role] = await users.GetRolesAsync(user);
        }

        return Ok(claims);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private enum ConsentOutcome
    {
        Granted,
        Required,
        Denied,
    }

    /// <summary>
    /// Why this request cannot complete on the existing browser session, or null when it can.
    /// </summary>
    private static string? RepromptReason(
        OpenIddictRequest request, AuthenticateResult cookie, ApplicationUser? user, StashedRequest? stashed)
    {
        if (user is null) return "no_session";

        if (request.HasPromptValue(PromptValues.Login) && stashed is not { Reprompted: true }) return "prompt_login";

        if (request.MaxAge is { } maxAge)
        {
            var authenticatedAt = SsoCookie.AuthenticatedAt(cookie.Principal);
            if (authenticatedAt is null) return "max_age_unknown";
            if (DateTimeOffset.UtcNow - authenticatedAt > TimeSpan.FromSeconds(maxAge)) return "max_age";
        }

        return null;
    }

    /// <summary>
    /// Resolves the cookie's subject to a live account, re-checking everything on every pass.
    /// </summary>
    private async Task<ApplicationUser?> ResolveSignedInUserAsync(AuthenticateResult cookie, CancellationToken ct)
    {
        if (!cookie.Succeeded) return null;

        var subject = SsoCookie.Subject(cookie.Principal);
        var sessionId = SsoCookie.SessionId(cookie.Principal);
        if (subject is null || sessionId is null) return null;

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsSigninAllowed() || user.EmailVerifiedAt is null) return null;

        var session = await ctx.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.IsRevoked || session.UserId != user.Id) return null;

        session.Touch();
        return user;
    }

    private async Task<List<object>> FindAuthorizationsAsync(
        string subject, string client, IEnumerable<string> requested, CancellationToken ct)
    {
        var found = new List<object>();

        await foreach (var authorization in authorizations.FindAsync(
                           subject: subject,
                           client: client,
                           status: Statuses.Valid,
                           type: AuthorizationTypes.Permanent,
                           scopes: [.. requested],
                           cancellationToken: ct))
        {
            found.Add(authorization);
        }

        return found;
    }

    private async Task<ConsentOutcome> ConsentDecisionAsync(
        object application, OpenIddictRequest request, StashedRequest? stashed,
        ApplicationUser user, List<object> existing, CancellationToken ct)
    {
        // A decision recorded against this exact request, by this exact person.
        if (stashed is { Confirmed: true } && stashed.DecidedBy == user.Id)
        {
            return stashed.ConsentGranted ? ConsentOutcome.Granted : ConsentOutcome.Denied;
        }

        if (request.HasPromptValue(PromptValues.Consent)) return ConsentOutcome.Required;

        var consentType = await applications.GetConsentTypeAsync(application, ct);

        // Implicit is what `firstParty: true` in AUTH_CLIENTS reconciles to.
        if (consentType is ConsentTypes.Implicit) return ConsentOutcome.Granted;

        if (existing.Count > 0) return ConsentOutcome.Granted;

        // External means consent is granted out of band and never by this screen; with nothing
        // stored there is nothing to fall back to.
        if (consentType is ConsentTypes.External) return ConsentOutcome.Denied;

        return ConsentOutcome.Required;
    }

    /// <summary>
    /// Parks the request if it is not parked already and sends the browser to a page on the static
    /// site, carrying nothing but the opaque id.
    /// </summary>
    private async Task<IActionResult> ParkAndRedirectAsync(
        string page, OpenIddictRequest request, string? stashId, StashedRequest? stashed,
        CancellationToken ct, bool markReprompted)
    {
        if (stashId is null || stashed is null)
        {
            stashId = await stash.StashAsync(AuthorizationRequestStash.AuthorizeKind, request, ct);
            stashed = await stash.PeekAsync(stashId, ct);
        }

        if (stashed is not null && markReprompted && !stashed.Reprompted)
        {
            stashed.Reprompted = true;
            await stash.SaveAsync(stashId, stashed, ct);
        }

        return Redirect($"{page}?{AuthorizationRequestStash.Parameter}={Uri.EscapeDataString(stashId)}");
    }

    /// <summary>
    /// One <see cref="LoginSession"/> per relying party, so "sign out of this one site" and "sign
    /// out everywhere" are two different buttons rather than the same one.
    /// </summary>
    private async Task<LoginSession> CreateClientSessionAsync(
        ApplicationUser user, object application, CancellationToken ct)
    {
        var displayName = await applications.GetDisplayNameAsync(application, ct)
                          ?? await applications.GetClientIdAsync(application, ct)
                          ?? "Website";

        var userAgent = Request.Headers.UserAgent.ToString();

        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = user.Id,
            DeviceName = $"{displayName} - {BrowserLabel.Describe(userAgent)}",
            DeviceType = DeviceType.Web,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
        });

        ctx.LoginSessions.Add(session);
        return session;
    }

    private static void CarryAuthenticationContext(
        ClaimsPrincipal principal, DateTimeOffset? authenticatedAt, IEnumerable<string> methods)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        if (authenticatedAt is not null)
        {
            identity.AddClaim(new Claim(Claims.AuthenticationTime,
                authenticatedAt.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }

        foreach (var method in methods)
        {
            identity.AddClaim(new Claim(Claims.AuthenticationMethodReference, method));
        }
    }

    /// <summary>
    /// Ends the browser session: clears the cookie, revokes the <see cref="LoginSession"/> behind
    /// it, then hands back to OpenIddict so <c>post_logout_redirect_uri</c> and <c>state</c> are
    /// honoured against the client's registered list rather than reimplemented here.
    /// </summary>
    private async Task<IActionResult> SignOutBrowserAsync(CancellationToken ct)
    {
        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        var sessionId = SsoCookie.SessionId(cookie.Principal);

        if (sessionId is not null)
        {
            var session = await ctx.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is { IsRevoked: false })
            {
                session.Revoke();
                await ctx.SaveChangesAsync(ct);
            }
        }

        await HttpContext.SignOutAsync(SsoCookie.Scheme);

        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties { RedirectUri = "/" });
    }

    private IActionResult ProtocolError(string error, string description) =>
        Forbid(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }));

    /// <summary>A resume link whose parked request is gone.</summary>
    private IActionResult Expired() => Redirect($"{LoginPage}?error=request_expired");
}
