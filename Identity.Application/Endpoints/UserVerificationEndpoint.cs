using Identity.Application.Services;
using Identity.Application.Templates;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

public class UserVerificationEndpoint
{
    
    [WolverineGet("api/v1/user/generate-verification-code")]
    public async Task<IResult> GenerateVerificationCode([FromQuery] string email, [NotBody] IDistributedCache cache, [NotBody] MicroserviceContext ctx, [NotBody] EmailService emailService)
    {
        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);
        if (user == null)
        {
            return Results.Accepted();
        }
        if(user.Email == null)
        {
            return Results.BadRequest("User email not found");
        }
        if(user.EmailConfirmed) return Results.BadRequest("User already verified");

        // Key the cache by the account's canonical email, not the caller-supplied
        // identifier - callers may pass the username (e.g. the post-login
        // "verify your email" prompt only has the login identifier on hand), and
        // that must resolve to the same cache entry the signup welcome email used.
        var verificationCode = await VerificationCodeService.GetOrCreateCodeAsync(cache, user.Email);
        var renderer = new EmailTemplateRenderer();

        var body = await renderer.RenderAsync("WelcomeEmail.cshtml", new WelcomeEmail()
        {
            Name = email,
            ConfirmationCode = verificationCode
        });

        
        await emailService.SendEmailAsync(user.Email, "Welcome to Venta.gg!", body);

        
        return Results.Ok();
    }
    
    [WolverineGet("api/v1/user/verify-email")]
    public async Task<IResult> VerifyEmail([FromQuery] string email, [FromQuery] string code, [NotBody] IDistributedCache cache, [NotBody] MicroserviceContext ctx, [NotBody] EmailService emailService)
    {
        var normalized = email.ToUpperInvariant();
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedEmail == normalized || x.NormalizedUserName == normalized);
        if (user == null)
        {
            return Results.Accepted();
        }
        if(user.EmailConfirmed) return Results.BadRequest("User already verified");

        var expectedCode = await cache.GetStringAsync($"verification_code:{user.Email}");
        if(expectedCode == null) return Results.BadRequest("Verification code not found");
        if(expectedCode != code) return Results.BadRequest("Invalid verification code");

        user.EmailConfirmed = true;
        user.EmailVerifiedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        return Results.Ok();
    }
}