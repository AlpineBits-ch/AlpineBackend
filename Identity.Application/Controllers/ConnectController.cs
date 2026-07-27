using System.Security.Claims;
using Identity.Application.Services.Steam;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Identity.Application.Controllers;

[ApiController]
[Route("connect")]
public class ConnectController(SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> manager, IDistributedCache cache, ILogger<ConnectController> logger) : ControllerBase
{
    [HttpPost("token")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ?? 
                      throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        ApplicationUser user;

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
            
            if (!await manager.CheckPasswordAsync(user, request.Password))
            {
                logger.LogInformation("The username {username} or password is incorrect", request.Username);
                return Unauthorized();
            }

            if (!user.IsSigninAllowed())
            {
                logger.LogInformation("User {username} is not allowed to sign in", request.Username);
                return Forbid("User is not allowed to sign in");
            }
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
                return Forbid("User is not allowed to sign in");
            }
            if (user.EmailVerifiedAt == null)
            {
                logger.LogInformation("User {username} is not verified", request.Username);
                return StatusCode(StatusCodes.Status403Forbidden, "Email not verified.");
                
            }

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
                return Forbid("User is not allowed to sign in");
            }
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
                return Forbid("Bot account is disabled.");
            }
        }
        else { return BadRequest("The grant type is not supported."); }

        // Create the ClaimsPrincipal
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        principal.SetClaim(OpenIddictConstants.Claims.Subject, await manager.GetUserIdAsync(user));
        principal.SetClaim(OpenIddictConstants.Claims.Email, user.Email);
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
}