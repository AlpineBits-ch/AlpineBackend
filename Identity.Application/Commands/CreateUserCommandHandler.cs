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

        // Reported as a flag, not as a validation failure. This is the second of two checks - the
        // caller has already looked, so reaching this means another request created the account in
        // between - and the response it produces has to be indistinguishable from a successful
        // signup. Returning an error string here is how "Email already exists" reached anonymous
        // callers in the first place.
        //
        // Note the early return happens before the password is hashed and before any consent is
        // recorded: the exists branch must create nothing at all, and a consent row for an account
        // this request did not create would be a false record.
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
        //
        // Written here rather than left to the client to POST afterwards: an account that exists
        // without a consent record is an account we cannot show ever agreed to anything, and putting
        // that on a second round trip means every dropped connection between the two leaves one. If
        // a version is superseded later, this row is untouched and the account simply shows up as
        // owing the new one (see ConsentService.GetOutstandingAsync).
        //
        // No SaveChangesAsync - this is a Wolverine handler and the transactional middleware commits.
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