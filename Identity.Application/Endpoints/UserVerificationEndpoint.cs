using Identity.Application.Services;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

/// <summary>
/// Email verification. Anonymous by necessity - the account cannot log in until it has been through
/// here, so there is no session to authorise against.
///
/// <para><b>Neither route may vary its answer on whether the address belongs to an account.</b> Both
/// used to: <c>generate-verification-code</c> returned 202 for an unknown address, 200 for a
/// registered unverified one and 400 "User already verified" for a registered verified one, which
/// let anyone read the registration status of an arbitrary email list off the status code without
/// logging in. That also walked straight around the discoverability controls in
/// <c>docs/specs/privacy.md</c> (T2-16), which go to some trouble to make "not discoverable" and
/// "does not exist" indistinguishable - answering the same question by another route, and one that
/// needs no account at all.</para>
/// </summary>
public class UserVerificationEndpoint
{
    /// <summary>
    /// The single refusal <see cref="VerifyEmail"/> gives for every code that does not work, whatever
    /// the reason.
    ///
    /// <para>Collapsing "expired", "wrong" and "too many attempts" into one string is not
    /// over-caution. Keeping them apart left a two-request oracle that survives the uniform 202
    /// below: ask for a code for the target address, then submit a wrong one. A registered
    /// unverified account now has a live code, so it answers "invalid"; an unregistered address has
    /// nothing cached and answers "expired". The distinction is worth less than the leak - a user
    /// who cannot get their code in has to request a new one in every one of these cases anyway.</para>
    /// </summary>
    private const string CodeRefused = "Invalid or expired verification code - request a new one.";

    /// <summary>
    /// Requests a verification code by email.
    ///
    /// <para>Always 202, with no body, for every caller. A code is minted and mailed only when the
    /// identifier resolves to an account that has an address and has not verified it yet; that work
    /// happens after the response (see <see cref="AccountEmailDispatcher"/>) so the two branches
    /// cannot be told apart by how long they take either.</para>
    ///
    /// <para><b>Breaking for clients that read the old 200 / 400 "User already verified" split.</b>
    /// There is no authenticated equivalent to preserve the precise answer behind: <c>/connect/token</c>
    /// refuses to issue tokens to an unverified account, so a caller on this journey is unauthenticated
    /// by definition and there is no session in which they could be told more. The honest client-side
    /// wording is "if that address needs verifying, we have sent a code".</para>
    /// </summary>
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

    /// <summary>
    /// Confirms an address with the emailed code.
    ///
    /// <para>Unlike the request route this one cannot answer uniformly - the caller genuinely needs
    /// to know whether their code was accepted. So the rule is narrower: an unknown identifier is
    /// answered exactly as a known account with an unusable code is, and every unusable-code reason
    /// shares one body (<see cref="CodeRefused"/>). An unknown address used to short-circuit to 202
    /// while a wrong code got a 400, which was the same oracle in a different shape.</para>
    ///
    /// <para><b>Breaking:</b> an unknown address now yields 400 rather than 202, and an
    /// already-verified account 400 rather than 400 "User already verified".</para>
    /// </summary>
    [WolverineGet("api/v1/user/verify-email")]
    public async Task<IResult> VerifyEmail([FromQuery] string? email, [FromQuery] string? code,
        [NotBody] IDistributedCache cache, [NotBody] MicroserviceContext ctx)
    {
        if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(CodeRefused);

        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);

        // No account, or an account with no address to confirm. Same answer a live account with a
        // dead code gets.
        if (user?.Email is null) return Results.BadRequest(CodeRefused);

        // Counts the attempt and destroys the code after too many wrong guesses - see
        // OneTimeCodeService. An already-verified account is not short-circuited above it: it simply
        // has no code outstanding, so it lands on the same refusal as everything else rather than
        // announcing its state.
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
