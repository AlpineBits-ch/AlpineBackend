using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Shared "does this local resource have any linked, Active remote instances" lookup used by
/// every outbound handler - the whole point of <see cref="FederatedResource"/> from the Phase 1
/// data model work.
/// </summary>
public static class FederatedResourceLookup
{
    public static Task<List<FederationInstance>> GetActiveInstancesAsync(
        MicroserviceContext db, FederatedResourceType resourceType, string localId, CancellationToken ct) =>
        db.FederatedResources
            .Include(r => r.Instance)
            .Where(r => r.ResourceType == resourceType
                        && r.LocalId == localId
                        && r.Instance.Status == FederationStatus.Active)
            .Select(r => r.Instance)
            .ToListAsync(ct);
}
