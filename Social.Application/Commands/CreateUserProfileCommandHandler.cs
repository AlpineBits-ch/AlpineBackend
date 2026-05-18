using Social.Contracts.Bus.Commands;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Commands;

public class CreateUserProfileCommandHandler
{
    public static CreateUserProfileCommandResponse Handle(CreateUserProfileCommand command, MicroserviceContext ctx, ILogger<CreateUserProfileCommandHandler> logger)
    {
        var profile = Profile.Create(new CreateProfileParams()
        {
            Username = command.Username,
            Bio = command.Bio,
            UserId = command.UserId,
        });

        ctx.Profiles.Add(profile);

        return new CreateUserProfileCommandResponse()
        {
            UserId = command.UserId,
            ProfileId = profile.Id,
        };
    }
}