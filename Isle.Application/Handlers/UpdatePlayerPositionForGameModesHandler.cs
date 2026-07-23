using System.Numerics;
using Isle.Api.Services;
using Isle.Contracts.Commands;

namespace Isle.Api.Handlers;

public class UpdatePlayerPositionForGameModesHandler
{
    public static void Handle(UpdatePlayerPositionCommand command, PlayerPositionCache cache)
    {
        cache.Update(
            command.PlayerId,
            new Vector3(command.WorldX, command.WorldY, command.WorldZ),
            command.Yaw);
    }
}