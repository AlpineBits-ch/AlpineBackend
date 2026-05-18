using FluentValidation;
using FluentValidation.Results;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Commands;
using Identity.Infrastructure.Persistence;
using Wolverine;
using WorkOS;

namespace Identity.Application.Handlers;

public class CreateUserWithUsernameAndPasswordHandler
{
    public async Task<CreateUserWithEmailAndPasswordResponse> Handle(CreateUserWithEmailAndPasswordRequest request, ILogger<CreateUserWithUsernameAndPasswordHandler> logger, IMessageBus messageBus, MicroserviceContext ctx)
    {
        try
        {
            if(ctx.Users.Any(u => u.Email == request.Username))
            {
                return new CreateUserWithEmailAndPasswordResponse()
                {
                    Failures =
                    [
                        new ValidationFailure("Email", "Email already exists")
                    ]
                };
               
            }
            logger.LogInformation("register user in with email {email} and password {password}", request.Email, request.Password);

            
            var user = await messageBus.InvokeAsync<CreateUserResponse>(new CreateUserCommand()
            {
                Username = request.Username,
                Email = request.Email,
                BirthDate = request.BirthDate,
                Password = request.Password,
            });

            if (user.UserId == null)
                throw new ValidationException(new List<ValidationFailure>()
                {
                    new ValidationFailure("General", "Could not create user")
                });

            return new CreateUserWithEmailAndPasswordResponse()
            {
                UserId = user.UserId
            };
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
           return new CreateUserWithEmailAndPasswordResponse()
           {
               Failures = new List<ValidationFailure>()
               {
                   new ValidationFailure("General", e.Message)
               }
           };
        }


       

    }
}