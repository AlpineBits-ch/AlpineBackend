using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

/// <summary>Identity's own participant in the AccountDeletionSaga fan-out - the actual
/// anonymize-in-place tombstone step. See ApplicationUser.Tombstone for why this alone is what
/// makes "Deleted User" show up everywhere without touching any other service's rows.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command, MicroserviceContext ctx)
    {
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == command.UserId);
        user?.Tombstone();

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "identity",
        };
    }
}
