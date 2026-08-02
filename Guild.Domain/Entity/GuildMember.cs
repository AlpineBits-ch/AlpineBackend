using System.ComponentModel.DataAnnotations.Schema;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildMemberParams
{
    public string GuildId { get; init; }
    public string UserId { get; init; }
    public MemberType Type { get; init; } = MemberType.Default;
    public string? Nickname { get; init; }
    public string? Bio { get; init; }
    public string? InviteId { get; init; }
    public string? InviteCode { get; init; }
    public string Username { get; init; }
}

public class GuildMember : BaseEntity<GuildMember>, IPrefixedEntity
{
    public Aggregates.Guild Guild { get; init; }
    public string GuildId { get; init; }
    public string UserId { get; init; }
    public DateTime JoinedAt { get; init; }
    [NotMapped] public static string Prefix { get; } = "gmbr";
    
    public MemberType Type { get; init; } = MemberType.Default;
    
    public string? Nickname { get; set; }
    public string? Bio { get; set; }

    public string? InviteId { get; init; }
    public GuildInvite? Invite { get; init; }

    /// <summary>Snapshot of the invite's <see cref="GuildInvite.Code"/> taken at join time. The
    /// <see cref="InviteId"/> FK is SetNull-on-delete, so deleting an invite drops the link but not
    /// the attribution - which is the whole value of the field for invite-tracking bots and the
    /// "who brought this member in" view. Null for members who did not join via an invite (owner,
    /// bot installs, federated shadow members).</summary>
    public string? InviteCode { get; init; }

    /// <summary>Uppercased haystack for the member-search endpoint's <c>Contains</c> query.
    /// Format is <c>USERNAME</c>, or <c>USERNAME\nNICKNAME</c> once a nickname is set - see
    /// <see cref="BuildSearchValue"/>. Every row written before nicknames were editable holds the
    /// bare username, which is exactly the one-segment case, so no backfill was needed.</summary>
    public string SearchValue { get; set; }

    /// <summary>Composes <see cref="SearchValue"/> so that a member is findable by either their
    /// account username or their guild nickname. The separator is a newline specifically because
    /// it cannot appear in either input, so a search term can never match across the boundary.</summary>
    public static string BuildSearchValue(string username, string? nickname) =>
        string.IsNullOrWhiteSpace(nickname)
            ? username.ToUpperInvariant()
            : $"{username.ToUpperInvariant()}\n{nickname.ToUpperInvariant()}";

    /// <summary>The username segment of <see cref="SearchValue"/>, needed to recompose it when
    /// only the nickname changes (the username itself is not stored separately on the member).</summary>
    public string SearchUsernamePart()
    {
        var separator = SearchValue.IndexOf('\n');
        return separator < 0 ? SearchValue : SearchValue[..separator];
    }


    public string? FederatedServerId { get; set; }

    // Guild-level permission overrides for this member, applied after role aggregation
    // and before channel/category overwrites. Allows granting or revoking specific
    // permissions independently of the member's roles.
    public Permissions AllowPermissions { get; set; } = Permissions.None;
    public Permissions DenyPermissions { get; set; } = Permissions.None;

    /// <summary>Text-chat timeout: while in the future, message/reaction/thread/voice-connect
    /// permissions are stripped regardless of role/overwrite grants (see
    /// GuildPermissionService.ComputePermissionsForUserAsync).</summary>
    public DateTimeOffset? MutedUntil { get; set; }

    /// <summary>Null while the guild's onboarding (rules acceptance) is still pending - same
    /// participation-permission stripping as MutedUntil applies until this is set. Defaults to
    /// "already completed" (non-null) so every construction path is safe-by-default; only
    /// InviteEndpoint's organic join flow explicitly nulls this out, and only when the guild
    /// actually has onboarding configured at join time.</summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; } = DateTimeOffset.UtcNow;

    public virtual ICollection<RoleMember> RoleMembers { get; set; } = [];
    public virtual ICollection<ChannelPermission> PermissionOverwrites { get; set; } = [];
    public virtual ICollection<ReadState> ReadStates { get; set; } = [];


    public static GuildMember CreateForUser(CreateGuildMemberParams parameters)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;

        return new GuildMember
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            JoinedAt = date,
            GuildId = parameters.GuildId,
            UserId = parameters.UserId,
            Bio = parameters.Bio,
            Nickname = parameters.Nickname,
            Type = parameters.Type,
            SearchValue = BuildSearchValue(parameters.Username, parameters.Nickname),
            InviteId = parameters.InviteId,
            InviteCode = parameters.InviteCode,
            // Onboarding only gates the organic invite-redemption join path (InviteEndpoint
            // constructs GuildMember directly and sets this explicitly) - bot installs and
            // federated shadow members created through this factory were never shown a rules
            // screen to begin with, so they shouldn't be silently participation-restricted.
            OnboardingCompletedAt = date,
        };
    }

}
