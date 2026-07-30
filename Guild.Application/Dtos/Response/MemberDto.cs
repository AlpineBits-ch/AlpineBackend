using Facet;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Social.Contracts.Dtos;

namespace Guild.Application.Dtos.Response;

/// <summary>Mirrors Social.Domain.Enums.OnlineStatus.</summary>
public enum OnlineStatus
{
    Offline,
    Hidden,
    Online,
    Idle,
    DoNotDisturb,
}

/// <summary>A member as seen by other members.</summary>
[Facet(typeof(GuildMember), nameof(GuildMember.Guild), nameof(GuildMember.RoleMembers), NestedFacets = [typeof(FlatInviteDto), typeof(FlatChannelPermissionDto), typeof(ReadStateDto)], MaxDepth = 1)]
public partial class MemberDto
{
    public OnlineStatus Status { get; set; }
    public ProfileDto? Profile { get; set; }
    public List<MemberRoleAssignmentDto> RoleMembers { get; set; } = [];
}

/// <summary>The caller's own membership.</summary>
[Facet(typeof(GuildMember), nameof(GuildMember.Guild),
    NestedFacets = [typeof(FlatInviteDto), typeof(FlatRoleMember), typeof(FlatChannelPermissionDto), typeof(ReadStateDto)], MaxDepth = 2)]
public partial class SelfMemberDto
{

}

[Facet(typeof(Role), Include = ["Id", "CreatedAt", "UpdatedAt", nameof(Role.Permissions)])]
public partial class FlatRoleDto
{
    
}



[Facet(typeof(GuildMember), Include = ["Id", "CreatedAt", "UpdatedAt", "UserId", "GuildId", "SearchValue", "Nickname"], NestedFacets = [typeof(FlatRoleDto)])]
public partial class FlatMemberDto
{
    
}

