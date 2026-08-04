using AppEnvironment;
using Identity.Application.Services;
using Identity.Contracts.Commands;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.AspNetCore.Identity;

namespace Identity.Application.Commands;

public class CreateUserCommandHandler
{
    public static async Task<CreateUserResponse> Handle(
        CreateUserCommand command,
        ILogger<CreateUserCommandHandler> logger,
        MicroserviceContext ctx,
        IPasswordHasher<ApplicationUser> passwordHasher,
        ConsentService consents)
    {
        var user = ApplicationUser.Create(new CreateUserParams()
        {
            Username = command.Username,
            Email = command.Email,
            BirthDate = command.BirthDate,
        });
        
        var env = Env.AuthConfiguration;

        if (!env.RequireUserEmailVerification)
        {
            user.EmailVerifiedAt = DateTime.UtcNow;
            user.EmailConfirmed = true;
        }

        // Reported as a flag, not as a validation failure.
        if (ctx.Users.Any(u => u.Email == user.Email))
        {
            return new CreateUserResponse()
            {
                EmailAlreadyExists = true,
            };
        }


        var password = passwordHasher.HashPassword(user, command.Password);

        user.SetPasswordHash(password);
        
        ctx.Users.Add(user);

        // T1-10. Registration records consent for the then-current Terms and Privacy versions, with
        // the address the signup came from.
        var now = DateTimeOffset.UtcNow;
        foreach (var document in await consents.GetCurrentDocumentsAsync(now))
        {
            if (!ConsentService.RequiredDocumentTypes.Contains(document.DocumentType)) continue;

            await consents.RecordAsync(user.Id, document.DocumentType, document.Version,
                command.IpAddress, now);

            logger.LogInformation("Recorded registration consent for {DocumentType} v{Version}",
                document.DocumentType, document.Version);
        }

        return new CreateUserResponse()
        {
            UserId = user.Id
        };
    }
}