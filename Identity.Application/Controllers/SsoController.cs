using System.Security.Claims;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services.Sso;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Application.Controllers;

/// <summary>
/// Everything the static sign-in site at <c>auth.venta.gg</c> talks to that is not OIDC protocol.
/// </summary>
[ApiController]
[Route("api/v1/sso")]
public class SsoController(
    UserManager<ApplicationUser> users,
    IOpenIddictApplicationManager applications,
    MicroserviceContext ctx,
    AuthorizationRequestStash stash,
    ILogger<SsoController> logger) : ControllerBase
{
    // ── the browser session ─────────────────────────────────────────────────

    /// <summary>Turns an access token into the SSO cookie.</summary>
    [HttpPost("session")]
    [Authorize]
    public async Task<IActionResult> CreateSession(CancellationToken ct)
    {
        var subject = User.GetClaim(Claims.Subject) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionId = User.FindFirstValue(VentaClaimDestinations.SessionId);

        if (subject is null || sessionId is null) return Unauthorized();

        // A bot's client-credentials token is a perfectly valid access token and has no business
        // establishing a browser session; there is no person behind it to sign in.
        if (User.FindFirstValue(VentaClaimDestinations.UserType) == nameof(UserType.Bot)) return Forbid();

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsSigninAllowed() || user.EmailVerifiedAt is null) return Forbid();

        var session = await ctx.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.IsRevoked || session.UserId != user.Id) return Unauthorized();

        // The grant recorded whatever User-Agent string arrived.
        var userAgent = Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            session.DeviceName = BrowserLabel.Describe(userAgent);
            session.DeviceType = DeviceType.Web;
        }

        session.Touch();
        await ctx.SaveChangesAsync(ct);

        // auth_time and amr come off the token rather than out of the request body - see
        // ConnectController, which stamps them per grant type.
        var authenticatedAt = ReadAuthenticationTime(User) ?? DateTimeOffset.UtcNow;
        var methods = User.FindAll(Claims.AuthenticationMethodReference).Select(claim => claim.Value).ToArray();

        await HttpContext.SignInAsync(
            SsoCookie.Scheme,
            SsoCookie.BuildPrincipal(user.Id, session.Id, authenticatedAt, methods),
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation("Established an SSO browser session for user {UserId} on session {SessionId}",
            user.Id, session.Id);

        return Ok(Describe(user, authenticatedAt, methods));
    }

    /// <summary>Who this browser is signed in as, if anyone.</summary>
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSession(CancellationToken ct)
    {
        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        var user = await ResolveAsync(cookie, ct);

        if (user is null)
        {
            if (cookie.Succeeded) await HttpContext.SignOutAsync(SsoCookie.Scheme);
            return Ok(new SsoSessionDto { SignedIn = false });
        }

        return Ok(Describe(user, SsoCookie.AuthenticatedAt(cookie.Principal), SsoCookie.Methods(cookie.Principal)));
    }

    /// <summary>
    /// Signs the browser out of the identity provider: clears the cookie and revokes the browser's
    /// session.
    /// </summary>
    [HttpDelete("session")]
    [AllowAnonymous]
    public async Task<IActionResult> DeleteSession(CancellationToken ct)
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
        return NoContent();
    }

    // ── the parked request ──────────────────────────────────────────────────

    /// <summary>
    /// What the sign-in or consent screen needs to know about the request that sent the browser
    /// here.
    /// </summary>
    [HttpGet("request/{rq}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRequest(string rq, CancellationToken ct)
    {
        var stashed = await stash.PeekAsync(rq, ct);
        if (stashed is null) return NotFound();

        string? displayName = null;
        string? logoUri = null;

        if (stashed.ClientId is not null)
        {
            var application = await applications.FindByClientIdAsync(stashed.ClientId, ct);
            if (application is not null)
            {
                displayName = await applications.GetDisplayNameAsync(application, ct);
                logoUri = await AuthClientRegistry.GetLogoUriAsync(applications, application, ct);
            }
        }

        var resume = stashed.Kind == AuthorizationRequestStash.LogoutKind
            ? "/connect/logout/resume"
            : "/connect/authorize/resume";

        return Ok(new SsoRequestDto
        {
            Kind = stashed.Kind,
            ClientId = stashed.ClientId,
            ClientName = displayName ?? stashed.ClientId,
            LogoUri = logoUri,
            Scopes = SsoScopeCatalog.Describe(stashed.Scopes),
            LoginHint = stashed.LoginHint,
            ForceLogin = stashed.Parameters.TryGetValue(Parameters.Prompt, out var prompt)
                         && prompt.Split(' ').Contains(PromptValues.Login),
            ResumeUrl = $"{resume}?{AuthorizationRequestStash.Parameter}={Uri.EscapeDataString(rq)}",
        });
    }

    /// <summary>
    /// Records the consent screen's answer against the parked request and hands back the one URL to
    /// navigate to.
    /// </summary>
    [HttpPost("consent")]
    [AllowAnonymous]
    public async Task<IActionResult> Consent([FromBody] SsoDecisionRequest request, CancellationToken ct)
    {
        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        var user = await ResolveAsync(cookie, ct);
        if (user is null) return Unauthorized();

        var stashed = await stash.PeekAsync(request.Rq, ct);
        if (stashed is null || stashed.Kind != AuthorizationRequestStash.AuthorizeKind) return NotFound();

        stashed.Confirmed = true;
        stashed.ConsentGranted = request.Granted;
        stashed.DecidedBy = user.Id;
        await stash.SaveAsync(request.Rq, stashed, ct);

        logger.LogInformation("User {UserId} {Decision} consent for client {ClientId}",
            user.Id, request.Granted ? "granted" : "refused", stashed.ClientId);

        return Ok(new SsoDecisionDto
        {
            RedirectUrl =
                $"/connect/authorize/resume?{AuthorizationRequestStash.Parameter}={Uri.EscapeDataString(request.Rq)}",
        });
    }

    /// <summary>Confirms an RP-initiated sign-out.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmLogout([FromBody] SsoDecisionRequest request, CancellationToken ct)
    {
        var cookie = await HttpContext.AuthenticateAsync(SsoCookie.Scheme);
        if (!cookie.Succeeded) return Unauthorized();

        var stashed = await stash.PeekAsync(request.Rq, ct);
        if (stashed is null || stashed.Kind != AuthorizationRequestStash.LogoutKind) return NotFound();

        stashed.Confirmed = true;
        stashed.DecidedBy = SsoCookie.Subject(cookie.Principal);
        await stash.SaveAsync(request.Rq, stashed, ct);

        return Ok(new SsoDecisionDto
        {
            RedirectUrl =
                $"/connect/logout/resume?{AuthorizationRequestStash.Parameter}={Uri.EscapeDataString(request.Rq)}",
        });
    }

    // ── internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Same rule as <c>AuthorizationController.ResolveSignedInUserAsync</c>: the cookie is an
    /// assertion about the past, never a standing permission.
    /// </summary>
    private async Task<ApplicationUser?> ResolveAsync(AuthenticateResult cookie, CancellationToken ct)
    {
        if (!cookie.Succeeded) return null;

        var subject = SsoCookie.Subject(cookie.Principal);
        var sessionId = SsoCookie.SessionId(cookie.Principal);
        if (subject is null || sessionId is null) return null;

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsSigninAllowed() || user.EmailVerifiedAt is null) return null;

        var session = await ctx.LoginSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        return session is null || session.IsRevoked || session.UserId != user.Id ? null : user;
    }

    private static SsoSessionDto Describe(ApplicationUser user, DateTimeOffset? authenticatedAt, string[] methods) =>
        new()
        {
            SignedIn = true,
            UserId = user.Id,
            Username = user.UserName,
            Email = user.Email,
            AuthenticationMethods = methods,
            AuthenticatedAt = authenticatedAt,
        };

    private static DateTimeOffset? ReadAuthenticationTime(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(Claims.AuthenticationTime);

        return long.TryParse(raw, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}
