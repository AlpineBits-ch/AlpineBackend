using Guild.Domain.Entity;
using Guild.Domain.Enums;
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

        // Not covered by the membership cascade: GuildDirectMessagePreference is keyed on
        // (UserId, GuildId) precisely so it survives leaving a guild, which means the purge has to
        // remove it explicitly or a deleted account leaves its contactability choices behind.
        var directMessagePreferences = await ctx.GuildDirectMessagePreferences
            .Where(p => p.UserId == command.UserId)
            .ToListAsync();
        ctx.GuildDirectMessagePreferences.RemoveRange(directMessagePreferences);

        // Personas are keyed the same way and survive leaving a guild for the same reason, so they
        // need the same explicit removal. The denormalised name and avatar stay on historic
        // messages, as with any other author display data.
        var personas = await ctx.Set<Persona>()
            .Where(p => p.Scope == PersonaScope.User && p.OwnerUserId == command.UserId)
            .ToListAsync();
        var personaIds = personas.Select(p => p.Id).ToList();

        var profiles = await ctx.Set<PersonaGuildProfile>()
            .Where(p => personaIds.Contains(p.PersonaId))
            .ToListAsync();
        ctx.Set<PersonaGuildProfile>().RemoveRange(profiles);

        // Both halves: the grants this user's personas carried, and the grants naming this user on
        // somebody else's guild persona.
        var grants = await ctx.Set<PersonaGrant>()
            .Where(g => personaIds.Contains(g.PersonaId) || g.UserId == command.UserId)
            .ToListAsync();
        ctx.Set<PersonaGrant>().RemoveRange(grants);

        var autoproxy = await ctx.Set<PersonaAutoproxyState>()
            .Where(a => a.UserId == command.UserId)
            .ToListAsync();
        ctx.Set<PersonaAutoproxyState>().RemoveRange(autoproxy);

        ctx.Set<Persona>().RemoveRange(personas);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "guild",
        };
    }
}
