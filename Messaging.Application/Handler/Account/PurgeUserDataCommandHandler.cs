using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Account;

/// <summary>Messaging's participant in the AccountDeletionSaga fan-out.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command, MicroserviceContext ctx)
    {
        var memberships = await ctx.Members
            .Where(m => m.UserId == command.UserId)
            .Include(m => m.Devices)
            .ToListAsync();

        foreach (var membership in memberships)
            ctx.MemberDevices.RemoveRange(membership.Devices);

        ctx.Members.RemoveRange(memberships);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "messaging",
        };
    }
}
