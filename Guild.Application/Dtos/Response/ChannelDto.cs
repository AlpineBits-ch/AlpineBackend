using Facet;
using Guild.Domain.Aggregates;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(Channel), NestedFacets = [typeof(CategoryDto), typeof(ChannelDto), typeof(GuildDto), typeof(ChannelPermissionDto)])]
public partial class ChannelDto
{
    
}