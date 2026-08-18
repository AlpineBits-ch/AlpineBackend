using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>Creates a persona. Scope is decided by the route, not the body.</summary>
public class CreatePersonaDto
{
    public required string Name { get; set; }

    /// <summary>Must be instance-hosted media - see PersonaDisplayGuard.</summary>
    public string? AvatarUrl { get; set; }

    public string? Pronouns { get; set; }

    /// <summary>Hex colour such as #4F8A6B.</summary>
    public string? Color { get; set; }

    public string? ShortBio { get; set; }
}

/// <summary>
/// A partial update: an omitted property leaves the persona's current value alone.
/// </summary>
public class UpdatePersonaDto
{
    public string? Name { get; set; }

    /// <summary>Omit to leave the avatar alone, send <c>null</c> to clear it.</summary>
    public Optional<string> AvatarUrl { get; set; }

    public Optional<string> Pronouns { get; set; }
    public Optional<string> Color { get; set; }
    public Optional<string> ShortBio { get; set; }

    /// <summary>Retiring stops the persona being selectable without touching historic messages.</summary>
    public bool? IsRetired { get; set; }
}

/// <summary>
/// Adopts a persona into a guild and sets its per-guild overrides; a PUT, so an omitted property
/// clears the override rather than leaving it.
/// </summary>
public class UpsertPersonaProfileDto
{
    /// <summary>Overrides the persona's name in this guild only.</summary>
    public string? DisplayName { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>A short suffix rendered after the display name, PluralKit's servertag.</summary>
    public string? Tag { get; set; }

    /// <summary>Typed before the message to speak as this persona, for example <c>M:</c>.</summary>
    public string? ProxyPrefix { get; set; }

    public string? ProxySuffix { get; set; }
}

/// <summary>Gives a role or a user the right to speak as a guild-scoped persona.</summary>
public class CreatePersonaGrantDto
{
    public string? RoleId { get; set; }
    public string? UserId { get; set; }
}

/// <summary>Sends an approval submission back for rework.</summary>
public class RequestPersonaChangesDto
{
    public required string Reason { get; set; }
}

/// <summary>
/// The channel's autoproxy state for the calling user. <see cref="PersonaId"/> is required for
/// <see cref="AutoproxyMode.Pinned"/> and ignored otherwise - under
/// <see cref="AutoproxyMode.Sticky"/> the server tracks whichever persona spoke last.
/// </summary>
public class SetAutoproxyDto
{
    public AutoproxyMode Mode { get; set; }
    public string? PersonaId { get; set; }
}

/// <summary>Creates this guild's character page for a persona.</summary>
public class CreatePersonaPageDto
{
    /// <summary>Defaults to the persona's name in this guild.</summary>
    public string? Title { get; set; }

    public string? CategoryId { get; set; }
}

/// <summary>What a pull from the reference copy should do.</summary>
public enum PersonaPagePullStrategy
{
    /// <summary>Return the diff and change nothing.</summary>
    Preview,

    /// <summary>Three-way merge that keeps local edits wherever the two sides disagree.</summary>
    KeepLocal,

    /// <summary>Discard local edits and take the reference copy verbatim.</summary>
    TakeUpstream,
}

/// <summary>Pulls the reference copy's page into this guild's copy.</summary>
public class PullPersonaPageDto
{
    public PersonaPagePullStrategy Strategy { get; set; } = PersonaPagePullStrategy.Preview;
}
