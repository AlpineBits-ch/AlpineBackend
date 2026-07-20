using Facet;
using Facet.Mapping;
using Social.Domain.Aggregate;

namespace Social.Api.Dtos.Response;

public class ProfileMapConfig : IFacetMapConfiguration<Profile, ProfileDto>
{
    public static void Map(Profile source, ProfileDto target)
    {
        target.AvatarUrl = $"https://api.venta.gg/api/v1/social/profiles/{source.Id}/avatar";
    }
}
[Facet(typeof(Profile), NestedFacets = [typeof(NestedRelationshipDto)], MaxDepth = 1, Configuration = typeof(ProfileMapConfig))]
public partial class ProfileDto
{
    public string AvatarUrl { get; set; }
}