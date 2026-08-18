using Persistence;

namespace Guild.Domain.Entity;

public class CreatePersonaGrantParams
{
    public string PersonaId { get; init; } = null!;

    /// <summary>Exactly one of this and <see cref="UserId"/>.</summary>
    public string? RoleId { get; init; }

    /// <summary>Exactly one of this and <see cref="RoleId"/>.</summary>
    public string? UserId { get; init; }
}

/// <summary>
/// Permission to speak as a guild-scoped persona, held either by a role or by one person. This is
/// the tooling RP servers currently substitute with a social convention that only staff use the
/// Narrator tupper.
/// </summary>
public class PersonaGrant : BaseEntity<PersonaGrant>, IPrefixedEntity
{
    public static string Prefix { get; } = "pgnt";

    public string PersonaId { get; set; } = null!;

    /// <summary>Null when the grant names a user.</summary>
    public string? RoleId { get; set; }

    /// <summary>Null when the grant names a role.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Builds a grant, rejecting the two shapes that would make it meaningless.
    /// </summary>
    /// <param name="params">The role or user to grant to.</param>
    /// <returns>The new grant.</returns>
    /// <exception cref="ArgumentException">Neither or both of the role and the user were given.</exception>
    public static PersonaGrant Create(CreatePersonaGrantParams @params)
    {
        var hasRole = !string.IsNullOrWhiteSpace(@params.RoleId);
        var hasUser = !string.IsNullOrWhiteSpace(@params.UserId);

        if (hasRole == hasUser)
        {
            throw new ArgumentException(
                "A persona grant names exactly one of a role or a user.", nameof(@params));
        }

        var date = DateTimeOffset.UtcNow;
        return new PersonaGrant
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            PersonaId = @params.PersonaId,
            RoleId = hasRole ? @params.RoleId : null,
            UserId = hasUser ? @params.UserId : null,
        };
    }

    /// <summary>Whether this grant reaches <paramref name="userId"/>, directly or through one of
    /// the roles they hold.</summary>
    /// <param name="userId">The caller.</param>
    /// <param name="roleIds">Every role the caller holds in the persona's guild.</param>
    /// <returns>True when the grant applies.</returns>
    public bool Matches(string userId, IReadOnlyCollection<string> roleIds) =>
        UserId is not null ? UserId == userId : RoleId is not null && roleIds.Contains(RoleId);
}
