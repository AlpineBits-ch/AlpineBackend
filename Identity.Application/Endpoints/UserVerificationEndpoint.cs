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
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedUserName == email.ToUpperInvariant());
        if (user == null)
        {
            return Results.Accepted();
        }
        if(user.Email == null)
        {
            return Results.BadRequest("User email not found");
        }
        if(user.EmailConfirmed) return Results.BadRequest("User already verified");
        
        var verificationCode = Guid.NewGuid().ToString("N").Substring(0, 6);
        await cache.SetStringAsync($"verification_code:{email}", verificationCode, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });
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
    public async Task<IResult> GenerateVerificationCode([FromQuery] string email, [FromQuery] string code, [NotBody] IDistributedCache cache, [NotBody] MicroserviceContext ctx, [NotBody] EmailService emailService)
    {
        var user = ctx.Users.FirstOrDefault(x => x.NormalizedUserName == email.ToUpperInvariant());
        if (user == null)
        {
            return Results.Accepted();
        }
        if(user.EmailConfirmed) return Results.BadRequest("User already verified");
        
        var expectedCode = await cache.GetStringAsync($"verification_code:{email}");
        if(expectedCode == null) return Results.BadRequest("Verification code not found");
        if(expectedCode != code) return Results.BadRequest("Invalid verification code");
        
        user.EmailConfirmed = true;
        user.EmailVerifiedAt = DateTime.UtcNow;
        
        return Results.Ok();
    }
}