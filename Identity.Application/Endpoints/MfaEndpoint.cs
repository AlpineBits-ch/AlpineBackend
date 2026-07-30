using System.Security.Claims;
using System.Text;
using System.Web;
using Identity.Application.Dtos.Request;
using Identity.Domain.Aggregates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

[Authorize]
public class MfaEndpoint
{
    private const string Issuer = "Venta";

    private static string BuildOtpAuthUri(string email, string unformattedKey)
    {
        var label = HttpUtility.UrlEncode($"{Issuer}:{email}");
        var issuer = HttpUtility.UrlEncode(Issuer);
        return $"otpauth://totp/{label}?secret={unformattedKey}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>Step 1 of enrollment - mints (or returns the still-pending) authenticator key.
    /// TwoFactorEnabled stays false until /enable confirms the user actually has it working.</summary>
    [WolverinePost("api/v1/user/mfa/enroll")]
    public async Task<IResult> Enroll([NotBody] ClaimsPrincipal principal, [NotBody] UserManager<ApplicationUser> manager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var user = await manager.FindByIdAsync(userId);
        if (user is null) return Results.Unauthorized();

        if (user.TwoFactorEnabled) return Results.BadRequest("MFA is already enabled - disable it first to re-enroll.");

        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await manager.ResetAuthenticatorKeyAsync(user);
            key = await manager.GetAuthenticatorKeyAsync(user);
        }

        return Results.Ok(new
        {
            secret = key,
            otpAuthUri = BuildOtpAuthUri(user.Email!, key!),
        });
    }

    /// <summary>Step 2 - proves the user actually scanned the QR / saved the secret by round-tripping
    /// a real code from their authenticator app before MFA is actually turned on.</summary>
    [WolverinePost("api/v1/user/mfa/enable")]
    public async Task<IResult> Enable(EnableMfaDto dto, [NotBody] ClaimsPrincipal principal, [NotBody] UserManager<ApplicationUser> manager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var user = await manager.FindByIdAsync(userId);
        if (user is null) return Results.Unauthorized();

        var isValid = await manager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, dto.Code);
        if (!isValid) return Results.BadRequest("Invalid code");

        await manager.SetTwoFactorEnabledAsync(user, true);
        var recoveryCodes = await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8);

        return Results.Ok(new { recoveryCodes });
    }

    [WolverinePost("api/v1/user/mfa/disable")]
    public async Task<IResult> Disable(DisableMfaDto dto, [NotBody] ClaimsPrincipal principal, [NotBody] UserManager<ApplicationUser> manager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var user = await manager.FindByIdAsync(userId);
        if (user is null) return Results.Unauthorized();

        if (!await manager.CheckPasswordAsync(user, dto.Password)) return Results.BadRequest("Incorrect password");

        await manager.SetTwoFactorEnabledAsync(user, false);
        // Invalidate the old secret too - re-enrolling later must not silently accept codes from
        // whatever app/device had the previous QR code.
        await manager.ResetAuthenticatorKeyAsync(user);

        return Results.Ok();
    }

    [WolverinePost("api/v1/user/mfa/recovery-codes")]
    public async Task<IResult> RegenerateRecoveryCodes(RegenerateMfaRecoveryCodesDto dto, [NotBody] ClaimsPrincipal principal, [NotBody] UserManager<ApplicationUser> manager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var user = await manager.FindByIdAsync(userId);
        if (user is null) return Results.Unauthorized();

        if (!user.TwoFactorEnabled) return Results.BadRequest("MFA is not enabled");
        if (!await manager.CheckPasswordAsync(user, dto.Password)) return Results.BadRequest("Incorrect password");

        var recoveryCodes = await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8);

        return Results.Ok(new { recoveryCodes });
    }
}
