using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>Guild's participant in the AccountDeletionSaga fan-out.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(
        PurgeUserDataCommand command, MicroserviceContext ctx, ILogger<PurgeUserDataCommandHandler> logger)
    {
        var ownedGuilds = await ctx.Guilds
            .Where(g => g.OwnerId == command.UserId)
            .ToListAsync();

        foreach (var guild in ownedGuilds)
        {
            var successor = await ctx.GuildMembers
                .Where(m => m.GuildId == guild.Id && m.UserId != command.UserId)
                .OrderBy(m => m.JoinedAt)
                .FirstOrDefaultAsync();

            if (successor is not null)
            {
                logger.LogInformation(
                    "Transferring ownership of guild {GuildId} from purged owner {UserId} to {SuccessorId}",
                    guild.Id, command.UserId, successor.UserId);
                guild.OwnerId = successor.UserId;
            }
            else
            {
                logger.LogWarning(
                    "Guild {GuildId} has no remaining members to transfer ownership to after owner {UserId} was purged - left orphaned",
                    guild.Id, command.UserId);
            }
        }

        var memberships = await ctx.GuildMembers
            .Where(m => m.UserId == command.UserId)
            .ToListAsync();
        ctx.GuildMembers.RemoveRange(memberships);

        var publicKeys = await ctx.PublicKeys
            .Where(k => k.UserId == command.UserId)
            .ToListAsync();
        ctx.PublicKeys.RemoveRange(publicKeys);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "guild",
        };
    }
}
