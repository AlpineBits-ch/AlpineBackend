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

/// <summary>Registration.</summary>
public class CreateUserWithUsernameAndPasswordHandler
{
    /// <summary>Usernames are a deliberate exception to the uniform answer.</summary>
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

            if (string.IsNullOrWhiteSpace(request.Email))
                return Refused(new ValidationFailure("Email", "Email cannot be empty"));

            if (string.IsNullOrWhiteSpace(request.Username))
                return Refused(new ValidationFailure("Username", "Username cannot be empty"));

            // The age floor is validated here rather than being left to ApplicationUser.Create,
            // which is only reached on the create branch.
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

            // The password is deliberately NOT logged.
            logger.LogInformation("Registering user with email {email}", request.Email);

            var user = await messageBus.InvokeAsync<CreateUserResponse>(new CreateUserCommand()
            {
                Username = request.Username,
                Email = request.Email,
                BirthDate = request.BirthDate,
                Password = request.Password,
                IpAddress = request.IpAddress,
            });

            // Lost a race with a concurrent registration of the same address.
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
            // The message is logged, not returned.
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
    /// </summary>
    private static async Task AcceptWithoutCreatingAsync(
        ApplicationUser existing,
        string? password,
        IAccountPasswordVerifier passwords,
        AccountEmailDispatcher mail,
        IDistributedCache cache)
    {
        await passwords.CheckDummyAsync(password);

        // An account with no address (a bot) cannot be told anything.
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
