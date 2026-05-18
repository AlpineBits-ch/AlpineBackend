using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(Category), nameof(Category.Guild), NestedFacets = [typeof(ChannelDto), typeof(ChannelPermissionDto)])]
public partial class CategoryDto
{
    
}