using Identity.Application.Templates;
using Messaging;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>Mints a one-time code and mails it, off the request path.</summary>
public class AccountEmailDispatcher(IServiceScopeFactory scopes, ILogger<AccountEmailDispatcher> logger)
{
    /// <summary>Mints the verification code for an address that genuinely needs verifying and mails
    /// it. Callers decide "needs verifying"; this does no existence checking of its own.</summary>
    public virtual async Task QueueVerificationCodeAsync(IDistributedCache cache, string email)
    {
        // Minted here, in front of the caller, not in the background task.
        var code = await VerificationCodeService.GetOrCreateCodeAsync(cache, email);

        Queue(email, async mail =>
        {
            var body = await new EmailTemplateRenderer().RenderAsync("WelcomeEmail.cshtml", new WelcomeEmail
            {
                Name = email,
                ConfirmationCode = code,
            });

            await mail.SendEmailAsync(email, "Welcome to Venta.gg!", body);
        });
    }

    /// <summary>Mints a password-reset code and mails it.</summary>
    public virtual async Task QueuePasswordResetCodeAsync(IDistributedCache cache, string email, string displayName)
    {
        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(cache, email);

        Queue(email, async mail =>
        {
            var body = await new EmailTemplateRenderer().RenderAsync("PasswordResetEmail.cshtml", new PasswordResetEmail
            {
                Name = displayName,
                Email = email,
                ResetCode = code,
            });

            await mail.SendEmailAsync(email, "Reset your Venta.gg password", body);
        });
    }

    /// <summary>
    /// Tells an address that already has an account that someone just tried to register it, and
    /// sends nothing at all once that address has had <see
    /// cref="RegistrationNoticeThrottle.MaxPerWindow"/> notices in the window.
    /// </summary>
    public virtual async Task QueueRegistrationAttemptNoticeAsync(
        IDistributedCache cache, string email, string displayName, bool accountAwaitsVerification)
    {
        if (!await RegistrationNoticeThrottle.TryAcquireAsync(cache, email))
        {
            // Info, not Warning: for a real address under attack this is the system working.
            logger.LogInformation(
                "Suppressed a registration-attempt notice: {Email} has reached its notice budget", email);
            return;
        }

        if (accountAwaitsVerification)
        {
            await QueueVerificationCodeAsync(cache, email);
            return;
        }

        Queue(email, async mail =>
        {
            var body = await new EmailTemplateRenderer().RenderAsync("RegistrationAttemptEmail.cshtml",
                new RegistrationAttemptEmail
                {
                    Name = displayName,
                    Email = email,
                });

            await mail.SendEmailAsync(email, "Someone tried to sign up with your email address", body);
        });
    }

    private void Queue(string email, Func<EmailService, Task> work)
    {
        // A fresh scope, not the request's: the request scope is disposed the moment the 202 is
        // written, which is the whole point, so anything resolved from it would be racing disposal.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider.GetRequiredService<EmailService>());
            }
            catch (Exception ex)
            {
                // Never rethrows.
                logger.LogError(ex, "Failed to send an account email to {Email}", email);
            }
        });
    }
}
