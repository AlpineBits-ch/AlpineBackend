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

    /// <summary>
    /// Snapshot of the invite's <see cref="GuildInvite.Code"/> taken at join time.
    /// </summary>
    public string? InviteCode { get; init; }

    /// <summary>
    /// This membership was granted by a <see cref="GuildInvite.Temporary"/> invite, so it ends when
    /// the account goes offline unless a role beyond @everyone has been granted in the meantime.
    /// </summary>
    public bool TemporaryMembership { get; init; }

    /// <summary>When this temporary membership is due to be swept, if it is.</summary>
    public DateTimeOffset? TemporaryEvictionDueAt { get; set; }

    /// <summary>
    /// Uppercased haystack for the member-search endpoint's <c>Contains</c> query.
    /// </summary>
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

    // Guild-level permission overrides for this member, applied after role aggregation and before
    // channel/category overwrites.
    public Permissions AllowPermissions { get; set; } = Permissions.None;
    public Permissions DenyPermissions { get; set; } = Permissions.None;

    // The ModulePermissions siblings of the two above, applied at the same point in resolution.
    public ModulePermissions AllowModulePermissions { get; set; } = ModulePermissions.None;
    public ModulePermissions DenyModulePermissions { get; set; } = ModulePermissions.None;

    /// <summary>Text-chat timeout: while in the future, message/reaction/thread/voice-connect
    /// permissions are stripped regardless of role/overwrite grants (see
    /// GuildPermissionService.ComputePermissionsForUserAsync).</summary>
    public DateTimeOffset? MutedUntil { get; set; }

    /// <summary>
    /// Null while the guild's onboarding (rules acceptance) is still pending - same
    /// participation-permission stripping as MutedUntil applies until this is set.
    /// </summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this member has agreed to show their phone number to the other people in this guild,
    /// so they can be paid back.
    /// </summary>
    public bool SharePhoneForPayments { get; set; }

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
