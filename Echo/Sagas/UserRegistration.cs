using System.Reflection.Metadata;
using Identity.Contracts.Bus.Events;
using Social.Contracts.Bus.Commands;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

public class UserRegistrationSaga : Saga
{
    public string Id { get; set; }
    public  CreateUserProfileCommand Start(UserCreatedEvent userCreated)
    {
        
        Id = userCreated.UserId;
        return new CreateUserProfileCommand()
        {
            UserId = userCreated.UserId,
            Username = userCreated.UserName,
            Bio = userCreated.Bio,
        };
        
    }
    
    public void Handle([SagaIdentityFrom(nameof(CreateUserProfileCommandResponse.UserId))]CreateUserProfileCommandResponse response, ILogger<UserRegistrationSaga> logger)
    {
        logger.LogInformation("user profile created {profileId}", response.ProfileId);
        MarkCompleted();   
    }
}