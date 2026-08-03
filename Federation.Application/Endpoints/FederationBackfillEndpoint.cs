using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Application.Security;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

/// <summary>
/// Split-brain recovery: if a receiver has a FederatedEventRecord buffered with Applied=false (its
/// parent never arrived - see FederationDagGcService), it can pull this instance's full applied
/// history for that scope and re-run it through RecordAndResolveAsync locally to fill the gap,
/// rather than waiting indefinitely or losing the branch to GC.
/// </summary>
public class FederationBackfillEndpoint
{
    [WolverineGet("api/v1/federation/events/{scopeKey}/backfill")]
    public static async Task<IResult> BackfillAsync(
        string scopeKey,
        [FromHeader(Name = FederationRequestSignature.HostHeader)] string? callerHost,
        [FromHeader(Name = FederationRequestSignature.TimestampHeader)] string? timestamp,
        [FromHeader(Name = FederationRequestSignature.SignatureHeader)] string? signature,
        MicroserviceContext db,
        ILogger<FederationBackfillEndpoint> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerHost))
            return Results.BadRequest($"{FederationRequestSignature.HostHeader} header is required.");

        var caller = await db.FederationInstances.FirstOrDefaultAsync(i => i.Host == callerHost, cancellationToken);
        if (caller is null || caller.Status != FederationStatus.Active)
            return Results.Forbid();

        if (!FederationRequestSignature.Verify(caller, scopeKey, timestamp, signature))
        {
            logger.LogWarning(
                "Rejected backfill for scope {ScopeKey}: bad or missing signature for claimed host {Host}.",
                scopeKey, callerHost);
            return Results.Forbid();
        }

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
