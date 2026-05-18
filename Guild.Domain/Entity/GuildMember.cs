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
    public string Hash { get; set; }
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

    // Guild-level permission overrides for this member, applied after role aggregation
    // and before channel/category overwrites. Allows granting or revoking specific
    // permissions independently of the member's roles.
    public Permissions AllowPermissions { get; set; } = Permissions.None;
    public Permissions DenyPermissions { get; set; } = Permissions.None;

    public virtual ICollection<RoleMember> RoleMembers { get; set; } = [];
    public virtual ICollection<ChannelPermission> PermissionOverwrites { get; set; } = [];
    public virtual ICollection<ReadState> ReadStates { get; set; } = [];


    public static GuildMember CreateForUser(CreateGuildMemberParams parameters)
    {
        var id = GenerateId();
        var searchValue = parameters.Username + "#" + parameters.Hash;

        return new GuildMember
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            JoinedAt = DateTime.UtcNow,
            UserId = parameters.UserId,
            Bio = parameters.Bio,
            Nickname = parameters.Nickname,
            Type = parameters.Type,
            SearchValue = searchValue.ToUpperInvariant(),
            InviteId = parameters.InviteId,
        };
    }

}
