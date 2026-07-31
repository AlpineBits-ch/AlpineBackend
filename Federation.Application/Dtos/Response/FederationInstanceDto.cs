using Facet;
using Federation.Domain.Aggregates;

namespace Federation.Application.Dtos.Response;

[Facet(typeof(FederationInstance), NestedFacets = [typeof(FederatedResourceDto)])]
public partial class FederationInstanceDto
{

}

/// <summary>A linked resource without its owning <see cref="FederationInstance"/>.</summary>
[Facet(typeof(FederatedResource), nameof(FederatedResource.Instance))]
public partial class FederatedResourceDto
{

}
