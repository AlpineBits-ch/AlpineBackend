using System.Security.Claims;
using Identity.Application.Services;
using System.Text.Json;
using Identity.Application.Services.Qr;
using Identity.Application.Services.Steam;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Identity.Application.Controllers;

[ApiController]
[Route("connect")]
public class ConnectController(SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> manager, IDistributedCache cache, MicroserviceContext ctx,
    Identity.Application.Services.IAccountPasswordVerifier passwords,
    SessionDeviceResolver deviceResolver,
    ILogger<ConnectController> logger) : ControllerBase
{
    [HttpPost("token")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
                      throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        ApplicationUser user;
        LoginSession session;

        if (request.IsPasswordGrantType())
        {
            user = await manager.FindByNameAsync(request.Username);
            if (user == null)
            {
                logger.LogInformation("User not found by email: {username}", request.Username);
            }
            if(user == null)
                return NotFound();

            if (user.EmailVerifiedAt == null)
            {
                logger.LogInformation("User {username} is not verified", request.Username);
                return StatusCode(StatusCodes.Status403Forbidden, "Email not verified.");

            }

            // Lockout-aware. Gating the *re-authentication* routes on a counting check while the
            // login itself did not count would have been theatre: an attacker would simply guess
            // here, unbounded, and carry the answer to whichever route they wanted.
            var check = await passwords.CheckAsync(user, request.Password);
            if (check == Services.PasswordCheckResult.LockedOut)
            {
                logger.LogInformation("Account {username} is locked out after repeated failures", request.Username);
                return StatusCode(StatusCodes.Status423Locked,
                    "Too many incorrect passwords. Try again later.");
            }

            if (!check.IsOk())
            {
                logger.LogInformation("The username {username} or password is incorrect", request.Username);
                return Unauthorized();
            }

            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("User {username} is not allowed to sign in", request.Username);
                return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to sign in");
            }

            var mfaFailure = await CheckSecondFactorAsync(user, request);
            if (mfaFailure is not null) return mfaFailure;

            session = await CreateSession(user, request, passwordProven: true);
        }
        else if (request.IsRefreshTokenGrantType())
        {
            // Authenticate the refresh token
            var info = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            user = await manager.GetUserAsync(info.Principal);
            if (user == null) return NotFound();
            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("User {username} is not allowed to sign in", request.Username);
                return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to sign in");
            }
            if (user.EmailVerifiedAt == null)
            {
                logger.LogInformation("User {username} is not verified", request.Username);
                return StatusCode(StatusCodes.Status403Forbidden, "Email not verified.");

            }

            // The access/refresh token pair carries the session_id claim set below the first
            // time this session was established - resolve it back to enforce revocation. A
            // missing claim means a token minted before session tracking existed; treat that
            // the same as a revoked session rather than silently granting an untracked refresh.
            var sessionId = info.Principal?.FindFirstValue("session_id");
            if (string.IsNullOrWhiteSpace(sessionId)) return Unauthorized();

            var existingSession = await ctx.LoginSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (existingSession is null || existingSession.IsRevoked) return Unauthorized();

            existingSession.Touch();
            session = existingSession;
        }
        else if (request.GrantType == SteamOpenIdService.SteamGrantType)
        {
            var ticket = (string?)request.GetParameter(SteamOpenIdService.TicketParameter);
            if (string.IsNullOrEmpty(ticket))
            {
                return BadRequest("The steam_ticket parameter is missing.");
            }

            // The ticket is single-use: consume it before issuing tokens.
            var cacheKey = SteamOpenIdService.LoginTicketCacheKey(ticket);
            var userId = await cache.GetStringAsync(cacheKey);
            if (userId == null)
            {
                logger.LogInformation("Steam login ticket not found or expired");
                return Unauthorized();
            }
            await cache.RemoveAsync(cacheKey);

            user = await manager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("User {userId} is not allowed to sign in", userId);
                return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to sign in");
            }

            // Applied to every interactive grant, not just the password one. MFA is an account-level
            // property: the whole point of the second factor is to survive compromise of one
            // credential, so signing in via a linked Steam account must not route around it. Same
            // reasoning for the email gate the password and refresh branches enforce.
            if (user.EmailVerifiedAt == null)
            {
                logger.LogInformation("User {userId} is not verified", userId);
                return StatusCode(StatusCodes.Status403Forbidden, "Email not verified.");
            }

            var steamMfaFailure = await CheckSecondFactorAsync(user, request);
            if (steamMfaFailure is not null) return steamMfaFailure;

            session = await CreateSession(user, request);
        }
        else if (request.GrantType == QrLoginService.QrGrantType)
        {
            var code = (string?)request.GetParameter(QrLoginService.CodeParameter);
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("The qr_code parameter is missing.");
            }

            // Single-use, same as the Steam ticket above: consume before acting so a redelivered
            // exchange request can't redeem the same approval twice.
            var cacheKey = QrLoginService.PairingCacheKey(code);
            var stateJson = await cache.GetStringAsync(cacheKey);
            if (stateJson is null) return Unauthorized();
            var state = JsonSerializer.Deserialize<QrPairingState>(stateJson);
            if (state is null || state.Status != QrPairingStatus.Approved || state.UserId is null)
            {
                return Unauthorized();
            }
            await cache.RemoveAsync(cacheKey);

            user = await manager.FindByIdAsync(state.UserId);
            if (user == null) return NotFound();

            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("User {userId} is not allowed to sign in", state.UserId);
                return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to sign in");
            }

            if (user.EmailVerifiedAt == null)
            {
                logger.LogInformation("User {userId} is not verified", state.UserId);
                return StatusCode(StatusCodes.Status403Forbidden, "Email not verified.");
            }

            // No second-factor prompt here, unlike the password and Steam grants: reaching an
            // Approved pairing state already required an authenticated session on an existing
            // device to scan and approve it, and that session passed MFA when it was established.
            // Possession of an already-trusted device is the factor.
            //
            // ClientDeviceId is caller-supplied on the anonymous qr-login/start route, so
            // CreateSession only honours it for a device row nobody has ever been - see its
            // passwordProven remarks.
            session = await CreateSession(user, request, deviceName: state.DeviceName, deviceType: state.DeviceType,
                clientDeviceId: state.ClientDeviceId);
        }
        else if (request.IsClientCredentialsGrantType())
        {
            // OpenIddict's server middleware already authenticated client_id/client_secret against
            // the registered OpenIddict application (Confidential + ClientCredentials permission)
            // before this action runs. A bot's own user id doubles as its OAuth client_id, so the
            // client_id here directly resolves to the bot's ApplicationUser.
            user = await manager.FindByIdAsync(request.ClientId);
            if (user == null) return NotFound();

            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("Bot {clientId} is not allowed to sign in", request.ClientId);
                return StatusCode(StatusCodes.Status403Forbidden, "Bot account is disabled.");
            }

            session = await CreateSession(user, request, deviceName: "Bot", deviceType: DeviceType.Web);
        }
        else { return BadRequest("The grant type is not supported."); }

        await ctx.SaveChangesAsync();

        // Create the ClaimsPrincipal
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        principal.SetClaim(OpenIddictConstants.Claims.Subject, await manager.GetUserIdAsync(user));
        principal.SetClaim(OpenIddictConstants.Claims.Email, user.Email);
        principal.SetClaim("session_id", session.Id);
        if (user.UserType == UserType.Bot)
        {
            // Lets every downstream service detect "is this caller a bot" straight from the
            // JWT, with no extra cross-service lookup (used e.g. to tag messages with
            // AuthorIdType.Bot).
            principal.SetClaim("user_type", nameof(UserType.Bot));
        }
        principal.SetScopes(request.GetScopes());
        // Tell OpenIddict which claims go into the JWT
        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
        }

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Builds (and stages, via <c>ctx.LoginSessions.Add</c>) a new LoginSession for a fresh
    /// login. Every grant type except refresh_token calls this exactly once; the refresh branch
    /// instead reuses and touches the session created by the original login. Device metadata is
    /// either caller-supplied (QR: from the pairing state; others: an optional device_name/
    /// device_type token-endpoint parameter) or falls back to the request's User-Agent header.
    ///
    /// <para>A <c>device_id</c> parameter (the same ClientDeviceId the client registers for MLS and
    /// sends as X-Device-Id) links the session to the device row, which is what lets revoking the
    /// session clear that device's push tokens. It is only linked if it really is one of this
    /// user's devices - an unknown id is ignored rather than rejected, because a first login
    /// necessarily happens before the device can be registered.</para>
    /// </summary>
    /// <param name="passwordProven">
    /// Whether this grant verified the account password. Binding a session to an <i>established</i>
    /// device row is what MlsDeviceEndpoint.BindSessionToDevice deliberately prices at the account
    /// password, because that binding decides whose encrypted backup the session may read. The
    /// password grant may therefore bind to any of the account's devices - the credential is the
    /// justification. The grants that prove no password (Steam, QR, client_credentials) may only
    /// bind to a row nobody has ever been, the same rule SessionDeviceResolver.TryClaimAsync
    /// applies; otherwise the caller-supplied device id was enough to seize the laptop's row and,
    /// with it, the laptop's backup.
    /// </param>
    /// <summary>
    /// Enforces the account's second factor. Returns null when the user may proceed, or the
    /// response to send back otherwise.
    ///
    /// Shared by every interactive grant. It previously lived inline in the password branch only,
    /// which meant an account with TOTP enabled could be signed into through the Steam grant with
    /// no second factor at all.
    /// </summary>
    private async Task<IActionResult?> CheckSecondFactorAsync(ApplicationUser user, OpenIddictRequest request)
    {
        if (!user.TwoFactorEnabled) return null;

        var mfaCode = (string?)request.GetParameter("mfa_code");
        if (string.IsNullOrWhiteSpace(mfaCode))
        {
            logger.LogInformation("User {userId} has MFA enabled but supplied no code", user.Id);
            return StatusCode(StatusCodes.Status401Unauthorized, "mfa_required");
        }

        var isValidTotp = await manager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, mfaCode);
        if (isValidTotp) return null;

        // Fall back to a recovery code - distinct format (8-char, one-time-use), so trying it only
        // after a failed TOTP check costs nothing on the common path.
        var recoveryResult = await manager.RedeemTwoFactorRecoveryCodeAsync(user, mfaCode);
        if (recoveryResult.Succeeded) return null;

        logger.LogInformation("Invalid MFA code for user {userId}", user.Id);
        return StatusCode(StatusCodes.Status401Unauthorized, "mfa_invalid");
    }

    private async Task<LoginSession> CreateSession(ApplicationUser user, OpenIddictRequest request,
        string? deviceName = null, DeviceType? deviceType = null, string? clientDeviceId = null,
        bool passwordProven = false)
    {
        var name = deviceName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = (string?)request.GetParameter("device_name");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            var ua = Request.Headers.UserAgent.ToString();
            name = string.IsNullOrWhiteSpace(ua) ? "Unknown device" : ua;
        }

        var type = deviceType;
        if (type is null)
        {
            var deviceTypeParam = (string?)request.GetParameter("device_type");
            type = Enum.TryParse<DeviceType>(deviceTypeParam, ignoreCase: true, out var parsed) ? parsed : DeviceType.Web;
        }

        var userAgentHeader = Request.Headers.UserAgent.ToString();

        var deviceId = clientDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = (string?)request.GetParameter("device_id");
        }
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = Request.Headers["X-Device-Id"].ToString();
        }

        string? deviceRowId = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var device = await ctx.UserDevices
                .FirstOrDefaultAsync(d => d.UserId == user.Id && d.ClientDeviceId == deviceId);

            if (device is not null)
            {
                // See the passwordProven remarks: without a password, only a row nobody has ever
                // been may be bound. An unknown or non-adoptable id is ignored rather than
                // rejected, matching the existing behaviour for a first login that necessarily
                // happens before the device is registered.
                if (passwordProven || await deviceResolver.IsAdoptableAsync(device))
                {
                    deviceRowId = device.Id;
                }
                else
                {
                    logger.LogInformation(
                        "Refusing to bind a session to established device {DeviceId} for user {UserId}: " +
                        "this grant proved no password.",
                        device.Id, user.Id);
                }
            }
        }

        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = user.Id,
            DeviceName = name,
            DeviceType = type.Value,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrWhiteSpace(userAgentHeader) ? null : userAgentHeader,
            DeviceId = deviceRowId,
        });

        ctx.LoginSessions.Add(session);
        return session;
    }
}
