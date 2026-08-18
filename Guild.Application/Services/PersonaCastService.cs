using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// How a guild's characters render to everybody, as opposed to which of them the caller may speak
/// as. Name, avatar and colour travel with whatever needs them - a scene's cast, a mention token -
/// so a client never resolves a persona one row at a time. Who plays a character is not part of
/// that: see docs/specs/roleplay-guilds.md §2.1.
/// </summary>
public class PersonaCastService(MicroserviceContext ctx)
{
    /// <summary>How these characters render in this guild.</summary>
    /// <param name="guildId">The guild being rendered.</param>
    /// <param name="personaIds">The characters to resolve.</param>
    /// <returns>Display data keyed by character, with characters this guild has not adopted absent.</returns>
    public async Task<Dictionary<string, PersonaCastMemberDto>> ResolveAsync(
        string guildId, IReadOnlyCollection<string> personaIds)
    {
        if (personaIds.Count == 0) return Empty();

        var ids = personaIds.ToList();

        return await BuildAsync(
            guildId,
            profiles => profiles.Where(p => ids.Contains(p.PersonaId)),
            includeRetired: true);
    }

    /// <summary>Every character this guild has adopted, which is the cast a client renders against.</summary>
    /// <param name="guildId">The guild being listed.</param>
    /// <param name="includeRetired">Whether characters that have been put away are listed too.</param>
    /// <returns>The cast, by name.</returns>
    public async Task<List<PersonaCastMemberDto>> ListAsync(string guildId, bool includeRetired = false)
    {
        var cast = await BuildAsync(guildId, profiles => profiles, includeRetired);

        return cast.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<Dictionary<string, PersonaCastMemberDto>> BuildAsync(
        string guildId,
        Func<IQueryable<PersonaGuildProfile>, IQueryable<PersonaGuildProfile>> narrow,
        bool includeRetired)
    {
        var profiles = await narrow(ctx.Set<PersonaGuildProfile>().AsNoTracking().Where(p => p.GuildId == guildId))
            .ToListAsync();

        if (profiles.Count == 0) return Empty();

        var adopted = profiles.Select(p => p.PersonaId).ToList();

        var personas = await ctx.Set<Persona>()
            .AsNoTracking()
            .Where(p => adopted.Contains(p.Id) && (includeRetired || !p.IsRetired))
            .ToListAsync();

        var byPersona = profiles.ToDictionary(p => p.PersonaId, StringComparer.Ordinal);

        return personas.ToDictionary(
            p => p.Id,
            p =>
            {
                var profile = byPersona.GetValueOrDefault(p.Id);
                return new PersonaCastMemberDto
                {
                    PersonaId = p.Id,
                    Name = PersonaDisplayGuard.EffectiveName(p, profile),
                    AvatarUrl = string.IsNullOrWhiteSpace(profile?.AvatarUrl) ? p.AvatarUrl : profile.AvatarUrl,
                    Color = p.Color,
                    Pronouns = p.Pronouns,
                    Tag = profile?.Tag,
                    IsRetired = p.IsRetired,
                };
            },
            StringComparer.Ordinal);
    }

    private static Dictionary<string, PersonaCastMemberDto> Empty() => new(StringComparer.Ordinal);
}
