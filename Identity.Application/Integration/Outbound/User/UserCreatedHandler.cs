using Identity.Contracts.Bus.Events;
using Identity.Domain.Events.User;

namespace Identity.Application.Integration.Outbound.User;

public class UserCreatedHandler
{
    public static UserCreatedEvent Handle(UserCreated userCreated)
    {
        return new UserCreatedEvent()
        {
            UserId = userCreated.UserId,
            Bio = userCreated.Bio,
            UserName = userCreated.UserName
        };
    }
}