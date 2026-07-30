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
    
    public string? Nickname { get; init; }
    public string? Bio { get; set; }

    public string? InviteId { get; init; }
    public GuildInvite? Invite { get; init; }
    
    public string SearchValue { get; set; }
    
    public string? FederatedServerId { get; set; }

    // Guild-level permission overrides for this member, applied after role aggregation and before
    // channel/category overwrites.
    public Permissions AllowPermissions { get; set; } = Permissions.None;
    public Permissions DenyPermissions { get; set; } = Permissions.None;

    /// <summary>Text-chat timeout: while in the future, message/reaction/thread/voice-connect
    /// permissions are stripped regardless of role/overwrite grants (see
    /// GuildPermissionService.ComputePermissionsForUserAsync).</summary>
    public DateTimeOffset? MutedUntil { get; set; }

    /// <summary>
    /// Null while the guild's onboarding (rules acceptance) is still pending - same
    /// participation-permission stripping as MutedUntil applies until this is set.
    /// </summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; } = DateTimeOffset.UtcNow;

    public virtual ICollection<RoleMember> RoleMembers { get; set; } = [];
    public virtual ICollection<ChannelPermission> PermissionOverwrites { get; set; } = [];
    public virtual ICollection<ReadState> ReadStates { get; set; } = [];


    public static GuildMember CreateForUser(CreateGuildMemberParams parameters)
    {
        var id = GenerateId();
        var searchValue = parameters.Username;
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
            SearchValue = searchValue.ToUpperInvariant(),
            InviteId = parameters.InviteId,
            // Onboarding only gates the organic invite-redemption join path (InviteEndpoint
            // constructs GuildMember directly and sets this explicitly) - bot installs and
            // federated shadow members created through this factory were never shown a rules
            // screen to begin with, so they shouldn't be silently participation-restricted.
            OnboardingCompletedAt = date,
        };
    }

}
