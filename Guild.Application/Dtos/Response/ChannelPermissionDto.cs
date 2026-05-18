using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(ChannelPermission), NestedFacets = [typeof(ChannelDto), typeof(ChannelPermissionDto), typeof(GuildDto), typeof(RoleDto)])]
public partial class ChannelPermissionDto
{
    
}