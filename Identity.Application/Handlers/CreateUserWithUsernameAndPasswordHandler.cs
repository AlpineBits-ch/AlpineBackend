using FluentValidation;
using FluentValidation.Results;
using Identity.Application.Services;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Validators;
using Identity.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Identity.Application.Handlers;

/// <summary>
/// Registration.
///
/// <para><b>The answer must not depend on whether the email already has an account.</b> This handler
/// used to return <c>Email already exists</c> for a taken address and a user id for a free one, which
/// turned the anonymous signup route into a membership oracle for any address list - the same
/// question <c>docs/specs/privacy.md</c> T2-16 goes to real trouble to make unanswerable for a
/// logged-in viewer, answered here with no account at all. It is now the last of the five anonymous
/// enumeration oracles in that spec to be closed.</para>
///
/// <para><b>The order of the checks below is load-bearing.</b> Every refusal this handler can still
/// give is decided <i>before</i> the address is looked up, so no refusal can be a function of whether
/// the address is registered. Move the exists check above them and the oracle comes straight back
/// through a side door: submit a taken username with the address you are probing and read the
/// account's existence off which of the two refusals comes back.</para>
/// </summary>
public class CreateUserWithUsernameAndPasswordHandler
{
    /// <summary>
    /// Usernames are a deliberate exception to the uniform answer.
    ///
    /// <para>They are discoverable by design in this product: <c>DiscoverableByUsername</c> defaults
    /// to true, and looking a username up is how friend requests are addressed - so "that name is
    /// taken" tells an attacker nothing an authenticated account cannot already establish, and no
    /// discoverability control is being routed around. Email is the opposite: nothing in the product
    /// lets one user resolve another's address, so registration would be the only place it leaked.</para>
    ///
    /// <para>The UX side is decisive on its own. A username is chosen at the keyboard and the server
    /// owns the namespace, so "pick another one" is information the user cannot obtain any other way
    /// and cannot proceed without. Collapsing it into the uniform 202 would silently drop
    /// registrations: the caller would be told to check an inbox that never receives anything.</para>
    /// </summary>
    private const string UsernameTaken = "That username is already taken.";

    public async Task<CreateUserWithEmailAndPasswordResponse> Handle(
        CreateUserWithEmailAndPasswordRequest request,
        ILogger<CreateUserWithUsernameAndPasswordHandler> logger,
        IMessageBus messageBus,
        MicroserviceContext ctx,
        IAccountPasswordVerifier passwords,
        IDistributedCache cache,
        AccountEmailDispatcher mail)
    {
        try
        {
            // ── Refusals that do not depend on the address ──────────────────────────────────
            //
            // These run first precisely so their answers are a function of the birth date and the
            // username alone. A caller probing an address gets the same refusal for it whether or
            // not that address is registered.

            if (string.IsNullOrWhiteSpace(request.Email))
                return Refused(new ValidationFailure("Email", "Email cannot be empty"));

            if (string.IsNullOrWhiteSpace(request.Username))
                return Refused(new ValidationFailure("Username", "Username cannot be empty"));

            // The age floor is validated here rather than being left to ApplicationUser.Create,
            // which is only reached on the create branch. Left there, an underage signup would be
            // refused for an unregistered address and accepted (202, silently doing nothing) for a
            // registered one - the oracle again, in the one shape a reader would never look for.
            var age = new AgeValidator().Validate(request.BirthDate);
            if (!age.IsValid) return Refused(age.Errors.ToArray());

            var normalizedUsername = request.Username.ToUpperInvariant();
            if (ctx.Users.Any(u => u.NormalizedUserName == normalizedUsername))
                return Refused(new ValidationFailure("Username", UsernameTaken));

            // ── The branch that must be invisible ───────────────────────────────────────────

            var normalizedEmail = request.Email.ToUpperInvariant();
            var existing = ctx.Users.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail);

            if (existing is not null)
            {
                await AcceptWithoutCreatingAsync(existing, request.Password, passwords, mail, cache);
                return new CreateUserWithEmailAndPasswordResponse();
            }

            // The password is deliberately NOT logged. This previously logged it in cleartext at
            // Information on every signup, and with Sentry wired up (AddErrorReporting in
            // Program.cs, whose default breadcrumb level is Information) that put every new user's
            // reusable password into a third-party system.
            logger.LogInformation("Registering user with email {email}", request.Email);

            var user = await messageBus.InvokeAsync<CreateUserResponse>(new CreateUserCommand()
            {
                Username = request.Username,
                Email = request.Email,
                BirthDate = request.BirthDate,
                Password = request.Password,
                IpAddress = request.IpAddress,
            });

            // Lost a race with a concurrent registration of the same address. Rare, but it has to
            // land on the same uniform answer as the checked branch above - otherwise the oracle
            // survives at a low duty cycle, which is worse than surviving outright because nothing
            // would ever notice it.
            if (user.EmailAlreadyExists)
            {
                var raced = ctx.Users.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail);
                if (raced is not null)
                    await AcceptWithoutCreatingAsync(raced, request.Password, passwords, mail, cache);

                return new CreateUserWithEmailAndPasswordResponse();
            }

            if (user.UserId == null)
                throw new ValidationException(new List<ValidationFailure>()
                {
                    new ValidationFailure("General", "Could not create user")
                });

            return new CreateUserWithEmailAndPasswordResponse();
        }
        catch (ValidationException e)
        {
            return new CreateUserWithEmailAndPasswordResponse()
            {
                Failures = new List<ValidationFailure>(e.Errors)
            };
        }
        catch (Exception e)
        {
            // The message is logged, not returned. This is an anonymous endpoint, and the exceptions
            // that reach here are mostly DbUpdateExceptions - a duplicate username trips the unique
            // index on NormalizedUserName - so echoing e.Message handed unauthenticated callers raw
            // Postgres constraint text, complete with table, column and index names.
            logger.LogError(e, "Registration failed for {email}", request.Email);

            return new CreateUserWithEmailAndPasswordResponse()
            {
                Failures = new List<ValidationFailure>()
                {
                    new ValidationFailure("General", "Could not create the account.")
                }
            };
        }
    }

    /// <summary>
    /// The already-registered branch: create nothing, tell the account holder, and cost the same as
    /// creating an account.
    ///
    /// <para><b>Nothing is written.</b> No user row, no password hash, and in particular no T1-10
    /// consent record - a consent row stamped with an anonymous stranger's IP, against an account
    /// they do not own, would be a fabricated legal record rather than a missing one.</para>
    ///
    /// <para><b>The dummy hash is not optional.</b> The create branch spends tens of milliseconds in
    /// PBKDF2 before it answers; returning from here without doing the same work makes the taken
    /// address reply arrive measurably sooner, and an attacker reads the answer off the clock
    /// instead of off the body. Same reason <c>Login</c> and <c>/connect/token</c> call it - see
    /// <see cref="IAccountPasswordVerifier.CheckDummyAsync"/>. What is left is the create branch's
    /// row inserts against this branch's Redis round trips: single-digit milliseconds against a
    /// ~100ms floor both branches pay, rather than the several hundred a mail send would add - which
    /// is why the mail is queued behind the response and not awaited.</para>
    /// </summary>
    private static async Task AcceptWithoutCreatingAsync(
        ApplicationUser existing,
        string? password,
        IAccountPasswordVerifier passwords,
        AccountEmailDispatcher mail,
        IDistributedCache cache)
    {
        await passwords.CheckDummyAsync(password);

        // An account with no address (a bot) cannot be told anything. It still gets the timing
        // treatment above and the same response.
        if (string.IsNullOrWhiteSpace(existing.Email)) return;

        await mail.QueueRegistrationAttemptNoticeAsync(
            cache,
            existing.Email,
            existing.UserName ?? existing.Email,
            accountAwaitsVerification: !existing.EmailConfirmed);
    }

    private static CreateUserWithEmailAndPasswordResponse Refused(params ValidationFailure[] failures) =>
        new() { Failures = failures.ToList() };
}
