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

        // The tombstone anonymizes the row in place rather than deleting it, so the FK cascades
        // never fire - which left a purged account's devices and push tokens alive, still receiving
        // calls and messages on a handset whose account no longer exists. Removing the devices
        // takes their key packages, backups and push tokens with them; the last delete covers
        // tokens that were registered without a device.
        var devices = await ctx.UserDevices.Where(d => d.UserId == command.UserId).ToListAsync();
        ctx.UserDevices.RemoveRange(devices);

        var tokens = await ctx.UserPushTokens.Where(t => t.UserId == command.UserId).ToListAsync();
        ctx.UserPushTokens.RemoveRange(tokens);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "identity",
        };
    }
}
