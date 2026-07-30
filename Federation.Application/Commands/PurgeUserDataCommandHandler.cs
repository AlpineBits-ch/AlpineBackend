using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Federation.Application.Commands;

/// <summary>Federation's participant in the AccountDeletionSaga fan-out.</summary>
public class PurgeUserDataCommandHandler
{
    public static Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command)
    {
        return Task.FromResult(new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "federation",
        });
    }
}
