using Facet;
using Social.Domain.Aggregate;

namespace Social.Api.Dtos.Response;

[Facet(typeof(Relationship), nameof(Relationship.Owner), nameof(Relationship.Target), nameof(Relationship.Related))]
public partial class NestedRelationshipDto
{
    
}