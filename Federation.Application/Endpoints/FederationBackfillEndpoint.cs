using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

/// <summary>
/// Split-brain recovery: if a receiver has a FederatedEventRecord buffered with Applied=false
/// (its parent never arrived - see FederationDagGcService), it can pull this instance's full
/// applied history for that scope and re-run it through RecordAndResolveAsync locally to fill
/// the gap, rather than waiting indefinitely or losing the branch to GC. Not wired up
/// automatically from the receiving side yet (detecting "this buffered event has been stuck too
/// long, go pull scopeKey from its origin host" is a real follow-up) - this is the serving side
/// of that recovery path.
///
/// Access is limited to registered Active instances (via the same X-Federated-Host convention
/// the main events endpoint's caller would recognize), not a full signature challenge like event
/// delivery - a backfill response only contains events that instance already received and
/// verified once, so replaying them again doesn't need re-proving authenticity, only that the
/// caller is a real federation partner and not an open scrape target.
/// </summary>
public class FederationBackfillEndpoint
{
    [WolverineGet("api/v1/federation/events/{scopeKey}/backfill")]
    public static async Task<IResult> BackfillAsync(
        string scopeKey,
        [FromHeader(Name = "X-Federated-Host")] string? callerHost,
        MicroserviceContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerHost))
            return Results.BadRequest("X-Federated-Host header is required.");

        var caller = await db.FederationInstances.FirstOrDefaultAsync(i => i.Host == callerHost, cancellationToken);
        if (caller is null || caller.Status != FederationStatus.Active)
            return Results.Forbid();

        var records = await db.FederatedEvents
            .Where(e => e.ScopeKey == scopeKey && e.Applied)
            .OrderBy(e => e.Depth)
            .ToListAsync(cancellationToken);

        var events = records
            .Select(r => JsonSerializer.Deserialize<FederationEvent>(r.PayloadJson)!)
            .ToList();

        return Results.Ok(events);
    }
}
