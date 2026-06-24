using AppEnvironment;
using FluentValidation.Results;
using Identity.Contracts.Commands;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.AspNetCore.Identity;

namespace Identity.Application.Commands;

public class CreateUserCommandHandler
{
    public static async Task<CreateUserResponse> Handle(CreateUserCommand command, ILogger<CreateUserCommandHandler> logger, MicroserviceContext ctx, IPasswordHasher<ApplicationUser> passwordHasher)
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

        if (ctx.Users.Any(u => u.Email == user.Email))
        {
            return new CreateUserResponse()
            {
                Errors = new List<ValidationFailure>()
                {
                    new ValidationFailure("Email", "Email already exists")
                }
            };
        }


        var password = passwordHasher.HashPassword(user, command.Password);

        user.SetPasswordHash(password);
        
        ctx.Users.Add(user);

        
        return new CreateUserResponse()
        {
            UserId = user.Id
        };
    }
}