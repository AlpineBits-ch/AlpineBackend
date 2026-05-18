using Facet;
using Social.Domain.Aggregate;

namespace Social.Api.Dtos.Response;

[Facet(typeof(Relationship), nameof(Relationship.Related), NestedFacets = [typeof(ProfileDto)])]
public partial class RelationshipDto
{
}