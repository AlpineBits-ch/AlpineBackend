using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Account;

/// <summary>
/// Messaging's participant in the AccountDeletionSaga fan-out. Deliberately does NOT touch
/// Message.AuthorId, Reaction.UserId, or Call/CallParticipant rows - none of those denormalize
/// author display data (Message.cs has no cached username/avatar field), so they keep resolving
/// live to the now-tombstoned Identity/Social rows and "Deleted User" shows up automatically,
/// the same mechanism Discord uses. Message/reaction content itself is neither owned solely by
/// the deleted user nor deletable without corrupting other participants' conversation history.
///
/// ConversationMember (and its per-device rows) IS removed, since that's active membership, not
/// historical content - same reasoning as Guild.GuildMember. This is what makes the deleted
/// user actually disappear from a DM/group's member list going forward.
/// </summary>
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
