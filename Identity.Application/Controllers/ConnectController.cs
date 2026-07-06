using Identity.Domain.Aggregates;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Identity.Application.Controllers;

[ApiController]
[Route("connect")]
public class ConnectController(SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> manager, ILogger<ConnectController> logger) : ControllerBase
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
        else { return BadRequest("The grant type is not supported."); }

        // Create the ClaimsPrincipal
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        principal.SetClaim(OpenIddictConstants.Claims.Subject, await manager.GetUserIdAsync(user));
        principal.SetClaim(OpenIddictConstants.Claims.Email, user.Email);
        principal.SetScopes(request.GetScopes());
        // Tell OpenIddict which claims go into the JWT
        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
        }

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}