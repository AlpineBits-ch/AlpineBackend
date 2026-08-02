using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services;
using Identity.Application.Templates;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>Mirrors UserVerificationEndpoint's shape (anonymous, no [Authorize] - the whole point
/// is the user can't log in) - same short-code-over-email pattern, distinct cache key prefix so it
/// can't be confused with an email-verification code for the same address.</summary>
public class PasswordResetEndpoint
{
    [WolverineGet("api/v1/user/request-password-reset")]
    public async Task<IResult> RequestPasswordReset([FromQuery] string email, [NotBody] IDistributedCache cache,
        [NotBody] MicroserviceContext ctx, [NotBody] EmailService emailService, [NotBody] ILogger<PasswordResetEndpoint> logger)
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

        try
        {
            await emailService.SendEmailAsync(user.Email, "Reset your Venta.gg password", body);
        }
        catch (Exception ex)
        {
            // Don't let a mail-delivery fault (e.g. Graph mail not configured in this environment,
            // or a transient outage) turn into an unhandled 500 that also contradicts the "always
            // look the same" contract above by revealing, via a failure response, that the account
            // exists.
            logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
        }

        return Results.Accepted();
    }

    [WolverinePost("api/v1/user/reset-password")]
    public async Task<IResult> ResetPassword(ResetPasswordDto dto, [NotBody] IDistributedCache cache,
        [NotBody] MicroserviceContext ctx, [NotBody] UserManager<ApplicationUser> manager,
        [NotBody] MasterKeyRewrapTicketService rewrapTickets)
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

        // The reset just made the password wrapping of the master key undecryptable: it is sealed
        // under Argon2(old password), and a reset is by definition the case where the user no
        // longer has that password.
        var mustRewrap = false;
        var historyLost = false;

        if (user.EncryptedMasterKey is not null && user.MasterKeyPasswordWrappingInvalidatedAt is null)
        {
            user.MasterKeyPasswordWrappingInvalidatedAt = DateTimeOffset.UtcNow;

            // A recovery-code wrapping still opens the key, so the client can re-wrap under the new
            // password and lose nothing. Without one, this is already unrecoverable.
            mustRewrap = user.RecoveryCodeWrappedMasterKey is not null;
            historyLost = user.RecoveryCodeWrappedMasterKey is null;

            ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
            {
                UserId = user.Id,
                Action = IdentityAuditActions.MasterKeyPasswordWrappingInvalidated,
                Detail = historyLost
                    ? "password reset with no recovery-code wrapping - encrypted history is unrecoverable"
                    : "password reset; client must re-wrap the master key from the recovery code",
            }));

            await ctx.SaveChangesAsync();
        }

        // The permit for the re-wrap this reset just made necessary.
        string? rewrapTicket = null;
        if (mustRewrap) rewrapTicket = await rewrapTickets.IssueAsync(user.Id);

        return Results.Ok(new ResetPasswordResultDto
        {
            MasterKeyRewrapRequired = mustRewrap,
            EncryptedHistoryRecoverable = !historyLost,
            MasterKeyRewrapTicket = rewrapTicket,
        });
    }
}
