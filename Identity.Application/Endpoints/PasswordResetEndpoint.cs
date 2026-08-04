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
    /// <summary>
    /// The single refusal for every reset code that does not work.
    ///
    /// <para>The three old messages ("Invalid code" for an unknown address, "Invalid or expired
    /// code" for a live account with nothing cached, "Too many incorrect attempts" once the counter
    /// tripped) told an anonymous caller apart from each other, and the first two differ precisely
    /// on whether the account exists. The attempt counter still destroys the code on the fifth wrong
    /// guess - only the wording it used to announce that with is gone, and "request a new one" is
    /// the right instruction in all three cases anyway.</para>
    /// </summary>
    private const string CodeRefused = "Invalid or expired code - request a new one.";

    /// <summary>
    /// Requests a reset code. Always 202, whoever asks.
    ///
    /// <para>The status code was already uniform; the <i>time</i> was not. Rendering the template and
    /// awaiting the Graph send only on the account-exists branch left a several-hundred-millisecond
    /// gap that answered the same question the response body refused to, so both now happen after
    /// the response - see <see cref="AccountEmailDispatcher"/>, which also absorbs the delivery
    /// faults the old inline try/catch was here to swallow.</para>
    /// </summary>
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

        // Evict every existing session. A password reset is the remedy a user reaches for when
        // they believe they are compromised, and without this it did not actually remove the
        // attacker: the refresh-token grant authorises on LoginSession.RevokedAt alone and never
        // consults the security stamp, so a stolen refresh token kept minting access tokens for
        // its full lifetime after the reset.
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
        // under Argon2(old password), and a reset is by definition the case where the user no longer
        // has that password. Nothing here can re-wrap it - the server never sees the master key -
        // so the honest thing is to record that it happened and tell the client.
        //
        // Without this the failure is completely silent. Every backup blob and the account identity
        // key stop being openable at the exact moment the user is trying to recover their account,
        // and nobody finds out until a restore fails months later.
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
        //
        // `rewrap-password` overwrites the wrapping that opens every backup blob on the account, so
        // it cannot be reachable on a bare session token - but on this journey there is by
        // definition no usable password to demand instead: the master key is sealed under the one
        // the user has just lost. What the caller does have is the emailed code they proved a moment
        // ago, and this carries that proof forward as a single-use, thirty-minute ticket.
        //
        // Only minted when a re-wrap can actually succeed. With no recovery-code wrapping the key is
        // already unrecoverable and a ticket would only invite a client to overwrite the wrapping
        // with bytes nothing opens.
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
