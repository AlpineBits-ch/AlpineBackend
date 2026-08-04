using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Guild's participant in the AccountDeletionSaga fan-out. Removes GuildMember rows outright
/// (active membership, not historical content - same reasoning as Social's Relationship
/// removal), which is what makes the deleted user actually disappear from member lists going
/// forward. GuildAuditLogEntry.ActorUserId and WikiRevision.AuthorId are deliberately left
/// untouched, same reasoning as Messaging's Message.AuthorId: neither denormalizes a display
/// name, so both keep resolving live to the tombstoned Identity/Social rows.
///
/// Guild.OwnerId needed real handling, not just a resolve-through-the-tombstone shrug: an owner
/// stops being able to authenticate the moment Identity flips their status off Active, so
/// leaving OwnerId pointing at them would leave the guild with a permanently unreachable owner.
/// Transfers to the longest-tenured remaining member (arbitrary but predictable choice - no
/// admin/mod role hierarchy concept exists on GuildMember to prefer instead); a guild with no
/// other members is left orphaned (logged) rather than auto-deleted, since silently destroying a
/// guild as a side effect of a member's account deletion is a bigger, separate call than this
/// fan-out should make unilaterally.
/// </summary>
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

        // Not covered by the membership cascade: GuildDirectMessagePreference is keyed on
        // (UserId, GuildId) precisely so it survives leaving a guild, which means the purge has to
        // remove it explicitly or a deleted account leaves its contactability choices behind.
        var directMessagePreferences = await ctx.GuildDirectMessagePreferences
            .Where(p => p.UserId == command.UserId)
            .ToListAsync();
        ctx.GuildDirectMessagePreferences.RemoveRange(directMessagePreferences);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "guild",
        };
    }
}
