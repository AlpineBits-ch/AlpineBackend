using System.Text;
using System.Text.RegularExpressions;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>One person a persona mention has to reach, and the character that reached them.</summary>
/// <param name="PersonaId">The character named in the message.</param>
/// <param name="UserId">Whoever answers for that character here.</param>
public sealed record PersonaMentionTarget(string PersonaId, string UserId);

/// <summary>
/// Typing <c>@Mayor Cogsgrove</c> notifies whoever plays the Mayor without telling anybody else who
/// that is. The character is the addressee on the wire and the owner is only ever a recipient, so
/// nothing this produces is carried in a payload another reader receives - see
/// docs/specs/roleplay-guilds.md §2.1.
/// </summary>
public class PersonaMentionService(MicroserviceContext ctx, PersonaService personas)
{
    /// <summary>
    /// The kind written into Messaging's mention index alongside <c>Direct</c> and <c>Here</c>. It
    /// is a distinct kind rather than a <c>Direct</c> row because a client rendering the Mentions
    /// tab has to show the character, not the account.
    /// </summary>
    public const string MentionKindName = "Persona";

    /// <summary>
    /// The mention token, following the <c>&lt;@id&gt;</c> grammar clients already parse. The
    /// <c>pers_</c> prefix is what keeps a character out of any list a user id is expected in.
    /// </summary>
    private static readonly Regex TokenPattern = new(
        @"<@(pers_[A-Za-z0-9]{1,64})>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every character named in a message body, in first-seen order.</summary>
    /// <param name="content">The plaintext body.</param>
    /// <returns>The distinct persona ids the body names.</returns>
    public static IReadOnlyList<string> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("<@", StringComparison.Ordinal)) return [];

        var seen = new List<string>();
        foreach (Match match in TokenPattern.Matches(content))
        {
            var id = match.Groups[1].Value;
            if (!seen.Contains(id, StringComparer.Ordinal)) seen.Add(id);
        }

        return seen;
    }

    /// <summary>The same, for the UTF-8 body the message-created event carries.</summary>
    /// <param name="content">The plaintext body as stored.</param>
    /// <returns>The distinct persona ids the body names.</returns>
    public static IReadOnlyList<string> Parse(byte[]? content)
    {
        if (content is null or { Length: 0 }) return [];

        return Parse(Encoding.UTF8.GetString(content));
    }

    /// <summary>
    /// Who a set of mentioned characters notifies in one guild.
    /// </summary>
    /// <param name="guildId">The guild the message was sent in.</param>
    /// <param name="authorId">The sender, who is never notified by their own message.</param>
    /// <param name="personaIds">The characters the message named.</param>
    /// <returns>One entry per person a named character reaches, deduplicated.</returns>
    public async Task<IReadOnlyList<PersonaMentionTarget>> ResolveAsync(
        string guildId, string authorId, IReadOnlyCollection<string> personaIds)
    {
        if (personaIds.Count == 0) return [];

        var ids = personaIds.ToList();

        // Adoption is what puts a character in a guild, so a persona nobody here has adopted is not
        // mentionable here however well-formed the token is.
        var adopted = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId && ids.Contains(p.PersonaId))
            .Select(p => p.PersonaId)
            .ToListAsync();

        if (adopted.Count == 0) return [];

        // Retired characters are excluded: retiring is how somebody puts a character away, and a
        // ping is exactly what they put away.
        var rows = await ctx.Set<Persona>()
            .AsNoTracking()
            .Where(p => adopted.Contains(p.Id) && !p.IsRetired)
            .Select(p => new { p.Id, p.Scope, p.OwnerUserId, p.OwnerGuildId })
            .ToListAsync();

        var targets = new List<PersonaMentionTarget>();

        foreach (var persona in rows)
        {
            if (persona.Scope == PersonaScope.User)
            {
                if (!string.IsNullOrWhiteSpace(persona.OwnerUserId))
                    targets.Add(new PersonaMentionTarget(persona.Id, persona.OwnerUserId));

                continue;
            }

            if (persona.OwnerGuildId != guildId) continue;

            // Every current grant holder, not the last speaker. A shared Narrator has no owner, so
            // the grant is the only recorded statement of who answers for it, and picking one
            // holder is how a scene stalls when a different GM is on duty.
            foreach (var userId in await personas.GranteeUserIdsAsync(guildId, persona.Id))
                targets.Add(new PersonaMentionTarget(persona.Id, userId));
        }

        return targets
            .Where(t => !string.Equals(t.UserId, authorId, StringComparison.Ordinal))
            .DistinctBy(t => (t.PersonaId, t.UserId))
            .ToList();
    }

    /// <summary>
    /// Who answers for each of these characters in this guild - the owner for a personal one, every
    /// current grant holder for a shared one.
    /// </summary>
    /// <param name="guildId">The guild the characters are adopted into.</param>
    /// <param name="personaIds">The characters to resolve.</param>
    /// <returns>Players keyed by character, with characters nobody answers for absent.</returns>
    public async Task<Dictionary<string, List<string>>> OwnersByPersonaAsync(
        string guildId, IReadOnlyCollection<string> personaIds)
    {
        // authorId is empty rather than a real user: the resolver drops the author from its own
        // results, and a nudge has no author to drop.
        var targets = await ResolveAsync(guildId, string.Empty, personaIds);

        return targets
            .GroupBy(t => t.PersonaId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(t => t.UserId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }
}
