using Identity.Application.Templates;
using Messaging;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// Mints a one-time code and mails it, off the request path.
///
/// <para><b>This exists for the timing channel, not for throughput.</b> The anonymous
/// "send me a code" routes (email verification, password reset) answer every caller with the same
/// 202 so the response body cannot say whether the address is registered - but if the registered
/// branch then blocks the response on a Microsoft Graph <c>sendMail</c> call plus a RazorLight
/// compile, the two branches differ by several hundred milliseconds and the status code was never
/// the leak that mattered. Doing the work after the response is what actually makes the two
/// indistinguishable.</para>
///
/// <para>The one-time code itself is still minted on the request path - see
/// <see cref="QueueVerificationCodeAsync"/>. Only the render and the send, which are the expensive
/// part, move behind the response. Note the renderer is constructed per call and caches compiled
/// templates in an instance field, so every send recompiles its template: a real cost, and another
/// reason it does not belong in front of the caller.</para>
///
/// <para>Deliberately fire-and-forget rather than a bus message: <c>ConfigureWolverine</c> routes
/// every published message over RabbitMQ (conventional routing, with local routing disabled), so
/// enqueuing here would put a broker hop and a durable-outbox insert between a user clicking
/// "resend" and their mail - and would still write to Postgres on the request path only in the
/// account-exists branch, reopening a smaller version of the same gap. A failed send is logged and
/// dropped; the code is already cached, so the user's next attempt reuses it (see
/// <see cref="OneTimeCodeService.GetOrCreateCodeAsync"/>, which is idempotent by design and must
/// stay that way - overwriting a live code is the regression that broke verification once already).
/// </para>
/// </summary>
public class AccountEmailDispatcher(IServiceScopeFactory scopes, ILogger<AccountEmailDispatcher> logger)
{
    /// <summary>Mints the verification code for an address that genuinely needs verifying and mails
    /// it. Callers decide "needs verifying"; this does no existence checking of its own.</summary>
    public virtual async Task QueueVerificationCodeAsync(IDistributedCache cache, string email)
    {
        // Minted here, in front of the caller, not in the background task. Two Redis round trips is
        // a sub-millisecond difference between the branches - unlike the send - and it means the
        // code is guaranteed to exist the moment the 202 is written, so nothing downstream depends
        // on a task that is by construction allowed to fail.
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

    /// <summary>Mints a password-reset code and mails it. <paramref name="displayName"/> is only
    /// used to address the mail.</summary>
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
    /// sends nothing at all once that address has had <see cref="RegistrationNoticeThrottle.MaxPerWindow"/>
    /// notices in the window.
    ///
    /// <para>This is the mail that lets <c>POST api/v1/authentication/register</c> answer a taken
    /// address exactly as it answers a free one. The old <c>400 "Email already exists"</c> told the
    /// caller; this tells the person who actually has a stake in knowing.</para>
    ///
    /// <para><paramref name="accountAwaitsVerification"/> switches the mail for the common honest
    /// case: a user who signed up, never found the code, and signed up again. They get the
    /// verification code they were missing instead of a notice about themselves, which is the only
    /// version of this flow that terminates for them. Both branches are invisible to the HTTP
    /// caller - the response is written before either runs - so choosing between them leaks
    /// nothing.</para>
    ///
    /// <para>The throttle is checked here, on the request path, and its answer is deliberately
    /// discarded rather than returned: a caller that could see "throttled" could distinguish a first
    /// attempt from a fourth, which is the same oracle at a lower resolution. Two Redis round trips,
    /// so it costs the response nothing measurable.</para>
    /// </summary>
    public virtual async Task QueueRegistrationAttemptNoticeAsync(
        IDistributedCache cache, string email, string displayName, bool accountAwaitsVerification)
    {
        if (!await RegistrationNoticeThrottle.TryAcquireAsync(cache, email))
        {
            // Info, not Warning: for a real address under attack this is the system working. It is
            // logged at all because a spike here is the signal that someone is being targeted.
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
                // Never rethrows. This runs with no request to fail and no caller to tell; an
                // unobserved task exception here would be an unhandled exception in the host.
                logger.LogError(ex, "Failed to send an account email to {Email}", email);
            }
        });
    }
}
