using Identity.Application.Services;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>Email verification.</summary>
public class UserVerificationEndpoint
{
    /// <summary>
    /// The single refusal <see cref="VerifyEmail"/> gives for every code that does not work,
    /// whatever the reason.
    /// </summary>
    private const string CodeRefused = "Invalid or expired verification code - request a new one.";

    /// <summary>Requests a verification code by email.</summary>
    [WolverineGet("api/v1/user/generate-verification-code")]
    public async Task<IResult> GenerateVerificationCode([FromQuery] string? email, [NotBody] MicroserviceContext ctx,
        [NotBody] IDistributedCache cache, [NotBody] AccountEmailDispatcher mail)
    {
        // Input validation survives: whether a caller sent an identifier at all does not depend on
        // whose identifier it is.
        if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest("email is required.");

        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);

        // Callers may pass the username rather than the address (the "verify your email" prompt only
        // has the login identifier on hand), so the mail and its cache entry are keyed by the
        // account's canonical email - that is the key the signup welcome email already used.
        if (user is { Email: not null, EmailConfirmed: false })
        {
            await mail.QueueVerificationCodeAsync(cache, user.Email);
        }

        return Results.Accepted();
    }

    /// <summary>Confirms an address with the emailed code.</summary>
    [WolverineGet("api/v1/user/verify-email")]
    public async Task<IResult> VerifyEmail([FromQuery] string? email, [FromQuery] string? code,
        [NotBody] IDistributedCache cache, [NotBody] MicroserviceContext ctx)
    {
        if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(CodeRefused);

        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);

        // No account, or an account with no address to confirm.
        if (user?.Email is null) return Results.BadRequest(CodeRefused);

        // Counts the attempt and destroys the code after too many wrong guesses - see
        // OneTimeCodeService.
        var codeResult = await VerificationCodeService.ValidateAsync(cache, user.Email, code);
        if (codeResult != OneTimeCodeResult.Valid) return Results.BadRequest(CodeRefused);

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            user.EmailVerifiedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        return Results.Ok();
    }
}
