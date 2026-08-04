using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>Mirrors UserVerificationEndpoint's shape (anonymous, no [Authorize] - the whole point
/// is the user can't log in) - same short-code-over-email pattern, distinct cache key prefix so it
/// can't be confused with an email-verification code for the same address.</summary>
public class PasswordResetEndpoint
{
    /// <summary>The single refusal for every reset code that does not work.</summary>
    private const string CodeRefused = "Invalid or expired code - request a new one.";

    /// <summary>Requests a reset code.</summary>
    [WolverineGet("api/v1/user/request-password-reset")]
    public async Task<IResult> RequestPasswordReset([FromQuery] string? email, [NotBody] IDistributedCache cache,
        [NotBody] MicroserviceContext ctx, [NotBody] AccountEmailDispatcher mail)
    {
        if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest("email is required.");

        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);

        // Always look the same to the caller whether or not the account exists - avoids leaking
        // which emails are registered.
        if (user?.Email is not null)
        {
            await mail.QueuePasswordResetCodeAsync(cache, user.Email, user.UserName ?? user.Email);
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

        // An unknown address is answered exactly as a live account with a dead code is.
        if (user?.Email is null) return Results.BadRequest(CodeRefused);

        // Counts the attempt and destroys the code after too many wrong guesses - a wrong answer
        // used to leave the code intact for its whole 15-minute life, so it could be guessed
        // indefinitely.
        var codeResult = await PasswordResetCodeService.ValidateAsync(cache, user.Email, dto.Code);
        if (codeResult != OneTimeCodeResult.Valid) return Results.BadRequest(CodeRefused);

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

        // ValidateAsync already consumed the code on success; this is belt-and-braces for the
        // case where the reset itself failed validation above and the caller retries.
        await PasswordResetCodeService.RemoveAsync(cache, user.Email);

        // Evict every existing session.
        var activeSessions = await ctx.LoginSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ToListAsync();

        foreach (var session in activeSessions) session.Revoke();

        if (activeSessions.Count > 0)
        {
            ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
            {
                UserId = user.Id,
                Action = IdentityAuditActions.SessionsRevoked,
                Detail = $"password reset revoked {activeSessions.Count} active session(s)",
            }));
        }

        await ctx.SaveChangesAsync();

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
