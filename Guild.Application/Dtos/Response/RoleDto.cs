using Facet;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(Role), nameof(Role.Guild), nameof(Role.Members), NestedFacets = [typeof(ChannelPermissionDto)])]
public partial class RoleDto
{
    
}