using Facet;
using Federation.Domain.Aggregates;

namespace Federation.Application.Dtos.Response;

[Facet(typeof(FederationInstance), NestedFacets = [typeof(FederatedResourceDto)])]
public partial class FederationInstanceDto
{

}

/// <summary>
/// A linked resource without its owning <see cref="FederationInstance"/>. The back-reference is
/// what closes the loop: the instance already carries the resource list, so serializing the
/// instance again from each resource walks straight back into the tracked graph.
/// </summary>
[Facet(typeof(FederatedResource), nameof(FederatedResource.Instance))]
public partial class FederatedResourceDto
{

}
