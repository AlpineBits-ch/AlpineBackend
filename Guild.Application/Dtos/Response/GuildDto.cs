using Facet;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(Domain.Aggregates.Guild), nameof(Domain.Aggregates.Guild.Members), nameof(Domain.Aggregates.Guild.Invites), NestedFacets = [typeof(ChannelDto), typeof(CategoryDto), typeof(RoleDto)], MaxDepth = 1)]
public partial class GuildDto
{
    
}