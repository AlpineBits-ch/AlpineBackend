using Facet;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Social.Contracts.Dtos;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// Mirrors Social.Domain.Enums.OnlineStatus. Kept as a local copy (rather than a
/// project reference into Social.Domain) to preserve the service boundary — Guild.Application
/// only depends on Social.Contracts. Must be kept in sync by hand whenever the Social-side
/// enum changes; previously missing "Hidden", which meant Enum.Parse&lt;OnlineStatus&gt;
/// would throw for any member whose real status was Hidden.
/// </summary>
public enum OnlineStatus
{
    Offline,
    Hidden,
    Online,
    Idle,
    DoNotDisturb,
}

[Facet(typeof(GuildMember), nameof(GuildMember.Guild), nameof(GuildMember.RoleMembers), NestedFacets = [typeof(InviteDto), typeof(ChannelPermissionDto), typeof(ReadStateDto)], MaxDepth = 1)]
public partial class MemberDto
{
    public OnlineStatus Status { get; set; }
    public ProfileDto? Profile { get; set; }
    public List<MemberRoleAssignmentDto> RoleMembers { get; set; } = [];
}

[Facet(typeof(GuildMember), nameof(GuildMember.Guild),
    NestedFacets = [typeof(InviteDto), typeof(FlatRoleMember), typeof(ChannelPermissionDto), typeof(ReadStateDto)], MaxDepth = 2)]
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

