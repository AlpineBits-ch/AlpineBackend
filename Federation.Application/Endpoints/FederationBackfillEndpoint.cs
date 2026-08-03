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
/// Split-brain recovery: if a receiver has a FederatedEventRecord buffered with Applied=false
/// (its parent never arrived - see FederationDagGcService), it can pull this instance's full
/// applied history for that scope and re-run it through RecordAndResolveAsync locally to fill
/// the gap, rather than waiting indefinitely or losing the branch to GC. Not wired up
/// automatically from the receiving side yet (detecting "this buffered event has been stuck too
/// long, go pull scopeKey from its origin host" is a real follow-up) - this is the serving side
/// of that recovery path.
///
/// Access is limited to registered Active instances, proven by an Ed25519 signature over
/// "host|scopeKey|timestamp" (see FederationRequestSignature) verified against the public key
/// already registered for that host.
///
/// The header alone is NOT sufficient and never was: X-Federated-Host is written by the caller, so
/// before the signature check any unauthenticated client could name an active peer and read that
/// peer's full applied history - message content, sender ids, and guild/conversation membership.
/// This endpoint is publicly routed by the gateway, so it must authenticate the caller itself.
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
