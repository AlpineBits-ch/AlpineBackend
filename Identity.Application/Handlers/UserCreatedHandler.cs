using AppEnvironment;
using Identity.Application.Services;
using Identity.Application.Templates;
using Identity.Domain.Events.User;
using Messaging;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Handlers;

public class UserCreatedHandler
{
    public async Task Handle(UserCreated userCreated, IDistributedCache cache, EmailService emailService)
    {
        if(!Env.AuthConfiguration.RequireUserEmailVerification) return;
        var email = userCreated.Email;
        var verificationCode = await VerificationCodeService.GetOrCreateCodeAsync(cache, email);
        var renderer = new EmailTemplateRenderer();

        var body = await renderer.RenderAsync("WelcomeEmail.cshtml", new WelcomeEmail()
        {
            Name = email,
            ConfirmationCode = verificationCode
        });

        
        await emailService.SendEmailAsync(email, "Welcome to Venta.gg!", body);
    }
}