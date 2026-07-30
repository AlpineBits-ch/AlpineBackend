using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Application.Templates;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>Mirrors UserVerificationEndpoint's shape (anonymous, no [Authorize] - the whole point
/// is the user can't log in) - same short-code-over-email pattern, distinct cache key prefix so it
/// can't be confused with an email-verification code for the same address.</summary>
public class PasswordResetEndpoint
{
    [WolverineGet("api/v1/user/request-password-reset")]
    public async Task<IResult> RequestPasswordReset([FromQuery] string email, [NotBody] IDistributedCache cache,
        [NotBody] MicroserviceContext ctx, [NotBody] EmailService emailService)
    {
        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);

        // Always look the same to the caller whether or not the account exists - avoids leaking
        // which emails are registered.
        if (user?.Email is null) return Results.Accepted();

        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(cache, user.Email);
        var renderer = new EmailTemplateRenderer();

        var body = await renderer.RenderAsync("PasswordResetEmail.cshtml", new PasswordResetEmail
        {
            Name = user.UserName ?? user.Email,
            Email = user.Email,
            ResetCode = code,
        });

        await emailService.SendEmailAsync(user.Email, "Reset your Venta.gg password", body);

        return Results.Accepted();
    }

    [WolverinePost("api/v1/user/reset-password")]
    public async Task<IResult> ResetPassword(ResetPasswordDto dto, [NotBody] IDistributedCache cache,
        [NotBody] MicroserviceContext ctx, [NotBody] UserManager<ApplicationUser> manager)
    {
        var normalized = dto.Email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);
        if (user?.Email is null) return Results.BadRequest("Invalid code");

        var expectedCode = await cache.GetStringAsync($"password_reset_code:{user.Email}");
        if (expectedCode is null || expectedCode != dto.Code) return Results.BadRequest("Invalid or expired code");

        // Our short code is the client-facing secret; Identity's own reset token is generated and
        // consumed entirely server-side in the same request so we still go through UserManager's
        // normal (password-policy-validating, security-stamp-rotating) reset path.
        var token = await manager.GeneratePasswordResetTokenAsync(user);
        var result = await manager.ResetPasswordAsync(user, token, dto.NewPassword);

        if (!result.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["newPassword"] = result.Errors.Select(e => e.Description).ToArray(),
            });
        }

        await PasswordResetCodeService.RemoveAsync(cache, user.Email);

        return Results.Ok();
    }
}
