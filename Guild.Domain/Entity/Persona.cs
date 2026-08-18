using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreatePersonaParams
{
    public PersonaScope Scope { get; init; } = PersonaScope.User;

    /// <summary>Set for <see cref="PersonaScope.User"/>, null otherwise.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>Set for <see cref="PersonaScope.Guild"/>, null otherwise.</summary>
    public string? OwnerGuildId { get; init; }

    public string Name { get; init; } = null!;
    public string? AvatarUrl { get; init; }
    public string? Pronouns { get; init; }
    public string? Color { get; init; }
    public string? ShortBio { get; init; }
}

/// <summary>
/// A character somebody writes as. It is a costume rather than a subject of authorization, so it
/// never gets a <c>GuildMember</c> row - see docs/specs/roleplay-guilds.md §2, which is also why the
/// id prefix is deliberately unlike a member's.
/// </summary>
public class Persona : BaseEntity<Persona>, IPrefixedEntity
{
    public static string Prefix { get; } = "pers";

    public PersonaScope Scope { get; set; } = PersonaScope.User;

    /// <summary>The owning account for a user-scoped persona. Null for a guild-scoped one, where
    /// there is no owning user at all and speaking rights come from <see cref="PersonaGrant"/>.</summary>
    public string? OwnerUserId { get; set; }

    /// <summary>The owning guild for a guild-scoped persona.</summary>
    public string? OwnerGuildId { get; set; }

    /// <summary>Which guild copy of the character page is currently the reference. Deliberately
    /// carries no foreign key: when the referenced guild is deleted the pointer is nulled and
    /// re-pointed by the same sweep, which is a promotion decision rather than a cascade.</summary>
    public string? HomeProfileId { get; set; }

    public string Name { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public string? Pronouns { get; set; }

    /// <summary>Hex, as on <c>Role.Color</c>.</summary>
    public string? Color { get; set; }

    public string? ShortBio { get; set; }

    /// <summary>Retired personas stop being selectable but keep rendering on historic messages and
    /// stay in the chronicle.</summary>
    public bool IsRetired { get; set; }

    /// <summary>Latched the first time the persona speaks and never cleared. Deleting is only
    /// allowed while this is false, so that <c>Message.PersonaId</c> can never dangle; a persona
    /// that has spoken retires instead. Stored rather than counted, because the messages live in
    /// another service.</summary>
    public bool HasSpoken { get; set; }

    public static Persona Create(CreatePersonaParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new Persona
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            Scope = @params.Scope,
            OwnerUserId = @params.Scope == PersonaScope.User ? @params.OwnerUserId : null,
            OwnerGuildId = @params.Scope == PersonaScope.Guild ? @params.OwnerGuildId : null,
            Name = @params.Name,
            AvatarUrl = @params.AvatarUrl,
            Pronouns = @params.Pronouns,
            Color = @params.Color,
            ShortBio = @params.ShortBio,
        };
    }

    /// <summary>Whether <paramref name="userId"/> owns this persona outright.</summary>
    public bool IsOwnedBy(string userId) =>
        Scope == PersonaScope.User && OwnerUserId == userId;

    /// <summary>
    /// Whether <paramref name="userId"/> may currently speak as this persona, given the roles they
    /// hold in the guild and the grants recorded against it.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="roleIds">Every role the caller holds in the persona's guild.</param>
    /// <param name="grants">The grants recorded against this persona.</param>
    /// <returns>True when the caller owns it or holds a matching grant.</returns>
    public bool CanBeSpokenBy(
        string userId, IReadOnlyCollection<string> roleIds, IEnumerable<PersonaGrant> grants)
    {
        // A retired persona is unselectable for everyone, owner included.
        if (IsRetired) return false;

        if (Scope == PersonaScope.User) return OwnerUserId == userId;

        return grants.Any(g => g.PersonaId == Id && g.Matches(userId, roleIds));
    }

    /// <summary>Marks the persona unselectable without touching anything it has already said.</summary>
    public void Retire()
    {
        IsRetired = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
