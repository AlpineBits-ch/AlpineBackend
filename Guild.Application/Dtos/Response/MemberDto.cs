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

/// <summary>
/// A member as seen by other members. Uses the flat invite/overwrite facets for the same reason
/// <see cref="SelfMemberDto"/> does: nesting <c>InviteDto</c> here projected
/// <c>source.Invite.Members</c> — every other member who joined on the same invite, as raw
/// GuildMember entities — into each row of the member list.
/// </summary>
[Facet(typeof(GuildMember), nameof(GuildMember.Guild), nameof(GuildMember.RoleMembers), NestedFacets = [typeof(FlatInviteDto), typeof(FlatChannelPermissionDto), typeof(ReadStateDto)], MaxDepth = 1)]
public partial class MemberDto
{
    public OnlineStatus Status { get; set; }
    public ProfileDto? Profile { get; set; }
    public List<MemberRoleAssignmentDto> RoleMembers { get; set; } = [];
}

/// <summary>
/// The caller's own membership. Every nested facet here is deliberately flat: this used to nest
/// <c>InviteDto</c> and <c>ChannelPermissionDto</c> at MaxDepth 2, which expanded into
/// Channel/Category/Role/Guild and their collections and made the endpoint 500 outright — see
/// <see cref="FlatChannelPermissionDto"/> for the mechanism.
///
/// MaxDepth stays 2 because <see cref="FlatRoleMember"/> needs one more level to reach its
/// <see cref="FlatRoleDto"/> (dropping to 1 silently nulls out every role). The depth cap is not
/// what keeps this query small — the facets being scalar-only is. Any facet added to the list
/// below has to hold that line itself.
/// </summary>
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

