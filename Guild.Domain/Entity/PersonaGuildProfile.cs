using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreatePersonaGuildProfileParams
{
    public string PersonaId { get; init; } = null!;
    public string GuildId { get; init; } = null!;
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Tag { get; init; }
    public string? ProxyPrefix { get; init; }
    public string? ProxySuffix { get; init; }
}

/// <summary>
/// One guild's copy of a persona: the overrides that make the same character read differently in a
/// different server, its local character page, and where it stands in that guild's approval queue.
/// </summary>
public class PersonaGuildProfile : BaseEntity<PersonaGuildProfile>, IPrefixedEntity
{
    public static string Prefix { get; } = "pgpf";

    public string PersonaId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>Overrides <c>Persona.Name</c> here only.</summary>
    public string? DisplayName { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>PluralKit's per-server <c>servertag</c>: a short suffix appended to the rendered
    /// name, usually naming the system or the faction.</summary>
    public string? Tag { get; set; }

    /// <summary>The affix that selects this persona when typing. Uniqueness spans this guild's
    /// profiles and every guild-scoped persona the sender holds a grant on, so it cannot be a
    /// unique index here and is enforced by the resolver on write.</summary>
    public string? ProxyPrefix { get; set; }

    public string? ProxySuffix { get; set; }

    /// <summary>This guild's character page, which is an ordinary wiki page.</summary>
    public string? WikiPageId { get; set; }

    /// <summary>The reference copy's revision number this page was last copied or pulled from.
    /// Null once the reference guild has been deleted and another copy promoted, at which point the
    /// remaining copies are diverged rather than behind.</summary>
    public int? UpstreamRevisionNumber { get; set; }

    public PersonaApprovalState ApprovalState { get; set; } = PersonaApprovalState.Draft;

    public string? ApprovedByUserId { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>The local page revision the approval was given against. Anything above it is a
    /// pending edit, which a reviewer sees as a diff and which does not stop the character being
    /// played.</summary>
    public int? LastApprovedRevisionNumber { get; set; }

    public string? ChangesRequestedReason { get; set; }

    public static PersonaGuildProfile Create(CreatePersonaGuildProfileParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new PersonaGuildProfile
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            PersonaId = @params.PersonaId,
            GuildId = @params.GuildId,
            DisplayName = @params.DisplayName,
            AvatarUrl = @params.AvatarUrl,
            Tag = @params.Tag,
            ProxyPrefix = @params.ProxyPrefix,
            ProxySuffix = @params.ProxySuffix,
        };
    }

    /// <summary>Sends the character to the guild's reviewers.</summary>
    /// <exception cref="InvalidOperationException">The profile is already submitted or approved.</exception>
    public void Submit()
    {
        if (ApprovalState is not (PersonaApprovalState.Draft or PersonaApprovalState.ChangesRequested))
        {
            throw new InvalidOperationException(
                $"A persona profile in {ApprovalState} cannot be submitted.");
        }

        ApprovalState = PersonaApprovalState.Submitted;
        ChangesRequestedReason = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Signs the character off at a given page revision.</summary>
    /// <param name="approverUserId">The reviewer.</param>
    /// <param name="revisionNumber">The local page revision being approved, or null when the
    /// profile has no page yet.</param>
    /// <exception cref="InvalidOperationException">Nothing was submitted for review.</exception>
    public void Approve(string approverUserId, int? revisionNumber)
    {
        if (ApprovalState != PersonaApprovalState.Submitted)
        {
            throw new InvalidOperationException(
                $"A persona profile in {ApprovalState} has nothing awaiting approval.");
        }

        ApprovalState = PersonaApprovalState.Approved;
        ApprovedByUserId = approverUserId;
        ApprovedAt = DateTimeOffset.UtcNow;
        LastApprovedRevisionNumber = revisionNumber;
        ChangesRequestedReason = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Sends the character back with a reason, leaving it resubmittable.</summary>
    /// <param name="reason">What the reviewer wants changed.</param>
    /// <exception cref="InvalidOperationException">Nothing was submitted for review.</exception>
    public void RequestChanges(string reason)
    {
        if (ApprovalState != PersonaApprovalState.Submitted)
        {
            throw new InvalidOperationException(
                $"A persona profile in {ApprovalState} has nothing awaiting review.");
        }

        ApprovalState = PersonaApprovalState.ChangesRequested;
        ChangesRequestedReason = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Whether the local page has been edited past what was signed off.</summary>
    /// <param name="currentRevisionNumber">The page's newest revision number.</param>
    /// <returns>True when a reviewer has a diff waiting.</returns>
    public bool HasUnapprovedChanges(int currentRevisionNumber) =>
        ApprovalState == PersonaApprovalState.Approved
        && LastApprovedRevisionNumber is { } approved
        && currentRevisionNumber > approved;

    /// <summary>Whether the character may be played here. A pending edit does not block speech:
    /// the approved revision keeps rendering, because blocking on a typo fix is how approval queues
    /// become resented.</summary>
    /// <param name="guildRequiresApproval">Whether the guild gates first use on approval.</param>
    /// <returns>True when the character is playable.</returns>
    public bool MaySpeak(bool guildRequiresApproval) =>
        !guildRequiresApproval || ApprovalState == PersonaApprovalState.Approved;
}
