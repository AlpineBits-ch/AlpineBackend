using System.Security.Claims;
using FluentValidation.Results;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Server.AspNetCore;
using Wolverine;

namespace Identity.Application.Controllers;

[ApiController]
[Route("api/v1/authentication")]
public class AuthenticationController(
    MicroserviceContext ctx,
    IMessageBus bus,
    SignInManager<ApplicationUser> signInManager,
    Identity.Application.Services.IAccountPasswordVerifier passwords,
    UserManager<ApplicationUser> manager) : ControllerBase
{
    private async Task<ApplicationUser?> FindUserByUsernameOrEmail(string usernameOrEmail)
    {
        var userByEmail = await ctx.Users.FirstOrDefaultAsync(u => u.Email == usernameOrEmail);
        if (userByEmail != null) return userByEmail;

        return await ctx.Users.FirstOrDefaultAsync(u => u.UserName == usernameOrEmail);

    }


    [HttpPost("verify")]
    public async Task<IActionResult> VerifyPassword(VerifyPasswordDto password)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest("claim not found");
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return NotFound("user not found");
        }

        // This route's entire purpose is "prove you are the account holder", so it is the single
        // most attractive place to grind passwords. Lockout-aware.
        var verify = await passwords.CheckAsync(user, password.Password);
        if (verify == Services.PasswordCheckResult.LockedOut)
            return StatusCode(StatusCodes.Status423Locked, "Too many incorrect passwords. Try again later.");
        if (!verify.IsOk()) return BadRequest("wrong password");

        return Ok();
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginWithEmailAndPasswordRequest request)
    {
        var user = await FindUserByUsernameOrEmail(request.Email);
        if (user is null)
        {
            // The response body already refused to distinguish "no such account" from "wrong
            // password"; the clock did not. Returning here without hashing anything made the
            // unknown-account reply arrive in a fraction of the time, which is the same oracle
            // measured differently.
            await passwords.CheckDummyAsync(request.Password);

            return Ok(new LoginWithEmailAndPasswordResponse()
            {
                Failures = new List<ValidationFailure>
                    { new ValidationFailure("Email", "Email or password is incorrect") }
            });
        }

        // The security stamp is NOT rotated here. This route is anonymous, and rotating before the
        // password is verified let anyone mutate any account's row by submitting a username - and
        // would invalidate that user's derived state on demand. Nothing about a failed login should
        // write to the account.
        if (!(await passwords.CheckAsync(user, request.Password)).IsOk())
        {
            return Ok(new LoginWithEmailAndPasswordResponse()
            {
                Failures = new List<ValidationFailure>
                    { new ValidationFailure("Email", "Email or password is incorrect") }
            });
        }

        // Same gates /connect/token applies. This path currently cannot complete (OpenIddict
        // rejects a SignIn outside a registered endpoint), but it must not read as a supported
        // login that skips account status, email verification and the second factor.
        if (!user.IsSigninAllowed() || user.EmailVerifiedAt is null || user.TwoFactorEnabled)
        {
            return Ok(new LoginWithEmailAndPasswordResponse()
            {
                Failures = new List<ValidationFailure>
                    { new ValidationFailure("Email", "Use /connect/token to sign in to this account.") }
            });
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return SignIn(principal: principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Creates an account, or pretends to.
    ///
    /// <para><b>Breaking, deliberately.</b> This used to answer <c>200 {"userId": "..."}</c> for a
    /// free address and <c>400 "Email already exists"</c> for a taken one. It now answers
    /// <c>202 Accepted</c> with <see cref="RegistrationAcceptedDto"/> in both cases, and there is no
    /// user id in the response at all - the client gets the account's id after it verifies the
    /// address and signs in. See <c>docs/specs/registration-contract-change.md</c>.</para>
    ///
    /// <para>Still 400: a birth date under the age floor, a taken username, a malformed or missing
    /// address, and an outright failure to create the account. None of those depend on whether the
    /// address is registered, which is what makes them safe to keep.</para>
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegistrationAcceptedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(IEnumerable<ValidationFailure>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(CreateUserRequest request)
    {
        var response = await bus.InvokeAsync<CreateUserWithEmailAndPasswordResponse>(
            new CreateUserWithEmailAndPasswordRequest()
            {
                Email = request.Email,
                Password = request.Password,
                BirthDate = DateOnly.FromDateTime(request.BirthDate),
                Username = request.Username,
                // Captured here because this is the only place in the registration path that has an
                // HTTP context. It ends up on the Terms/Privacy consent rows the create handler
                // writes (T1-10) - a consent record without an origin is materially weaker evidence.
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            }, timeout: TimeSpan.FromSeconds(15));
        if (response.Failures.Any())
        {
            return BadRequest(response.Failures);
        }

        // StatusCode rather than Accepted(): the Accepted overloads can stamp a Location header, and
        // a header that is present on one branch and absent on the other is exactly the kind of
        // difference the body was cleaned up to remove. A single shared constant instance, so there
        // is nothing per-request to vary.
        return StatusCode(StatusCodes.Status202Accepted, RegistrationAcceptedDto.Instance);
    }
}
    
    