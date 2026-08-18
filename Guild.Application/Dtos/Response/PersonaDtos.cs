using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>A character, independent of any guild it has been adopted into.</summary>
public class PersonaDto
{
    public string Id { get; set; } = null!;
    public PersonaScope Scope { get; set; }
    public string? OwnerUserId { get; set; }
    public string? OwnerGuildId { get; set; }
    public string Name { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public string? Pronouns { get; set; }
    public string? Color { get; set; }
    public string? ShortBio { get; set; }
    public bool IsRetired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static PersonaDto From(Persona persona) => new()
    {
        Id = persona.Id,
        Scope = persona.Scope,
        OwnerUserId = persona.OwnerUserId,
        OwnerGuildId = persona.OwnerGuildId,
        Name = persona.Name,
        AvatarUrl = persona.AvatarUrl,
        Pronouns = persona.Pronouns,
        Color = persona.Color,
        ShortBio = persona.ShortBio,
        IsRetired = persona.IsRetired,
        CreatedAt = persona.CreatedAt,
    };
}

/// <summary>How one persona is set up inside one guild.</summary>
public class PersonaGuildProfileDto
{
    public string PersonaId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Tag { get; set; }
    public string? ProxyPrefix { get; set; }
    public string? ProxySuffix { get; set; }
    public string? WikiPageId { get; set; }
    public int? UpstreamRevisionNumber { get; set; }

    /// <summary>One of <c>current</c>, <c>behind</c> or <c>diverged</c> - see PersonaPageService.</summary>
    public string UpstreamState { get; set; } = PersonaUpstreamState.Current;

    public PersonaApprovalState ApprovalState { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ChangesRequestedReason { get; set; }

    /// <summary>Whether the calling user may speak as this persona here, right now.</summary>
    public bool CanSpeak { get; set; }

    /// <summary>The persona itself, so a client listing a guild's cast needs one call.</summary>
    public PersonaDto? Persona { get; set; }
}

/// <summary>The three values <see cref="PersonaGuildProfileDto.UpstreamState"/> takes.</summary>
public static class PersonaUpstreamState
{
    public const string Current = "current";
    public const string Behind = "behind";
    public const string Diverged = "diverged";
}

/// <summary>One role's or one user's right to speak as a guild-scoped persona.</summary>
public class PersonaGrantDto
{
    public string Id { get; set; } = null!;
    public string PersonaId { get; set; } = null!;
    public string? RoleId { get; set; }
    public string? UserId { get; set; }

    public static PersonaGrantDto From(PersonaGrant grant) => new()
    {
        Id = grant.Id,
        PersonaId = grant.PersonaId,
        RoleId = grant.RoleId,
        UserId = grant.UserId,
    };
}

/// <summary>
/// The answer to DELETE on a persona, which retires rather than deletes once the persona has
/// spoken.
/// </summary>
public class PersonaDeletionDto
{
    public string PersonaId { get; set; } = null!;

    /// <summary>True when the persona was retired instead of deleted.</summary>
    public bool Retired { get; set; }

    /// <summary>Why, in one sentence, so a client can say it without inventing copy.</summary>
    public string? Reason { get; set; }
}

/// <summary>A channel's autoproxy state for one user.</summary>
public class AutoproxyDto
{
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public AutoproxyMode Mode { get; set; }

    /// <summary>The latched persona, or under Sticky whichever persona spoke here last.</summary>
    public string? PersonaId { get; set; }
}

/// <summary>The result of pulling a character page from its reference copy.</summary>
public class PersonaPagePullDto
{
    public string PersonaId { get; set; } = null!;
    public string? PageId { get; set; }

    /// <summary>Whether the merge was written, which a <c>preview</c> never is.</summary>
    public bool Applied { get; set; }

    public string UpstreamState { get; set; } = PersonaUpstreamState.Current;
    public int? UpstreamRevisionNumber { get; set; }
    public int? ReferenceRevisionNumber { get; set; }

    /// <summary>The merged body, so a preview shows exactly what applying would write.</summary>
    public string MergedContent { get; set; } = string.Empty;

    /// <summary>Merged infobox, opaque JSON to everything but the infobox renderer.</summary>
    public string? MergedInfoboxJson { get; set; }

    /// <summary>Lines the two sides changed differently, kept local and listed here.</summary>
    public List<PersonaPageConflictDto> Conflicts { get; set; } = [];
}

/// <summary>One line the local copy and the reference copy both changed.</summary>
public class PersonaPageConflictDto
{
    public int LineNumber { get; set; }
    public string? Base { get; set; }
    public string? Local { get; set; }
    public string? Upstream { get; set; }
}
