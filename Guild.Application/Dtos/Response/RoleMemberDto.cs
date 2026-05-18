using Facet;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;



[Facet(typeof(RoleMember), exclude: [nameof(RoleMember.Role)], NestedFacets = [typeof(FlatMemberDto)], MaxDepth = 2)]
public partial class RoleMemberDto
{
    
}

[Facet(typeof(RoleMember), Include = ["Id", "CreatedAt", "UpdatedAt", "Role"], NestedFacets = [typeof(FlatRoleDto)])]
public partial class FlatRoleMember
{
    
}
