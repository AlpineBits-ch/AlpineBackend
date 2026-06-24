using Federation.Application.Dtos.Requests;
using Federation.Application.Dtos.Response;
using Federation.Application.Messages;
using Federation.Application.Services;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

public class FederationHandshakeEndpoint
{
    [WolverinePost("/.well-known/federation/handshake")]
    public async Task<(IResult, FederationHandshakeReceived?)> HandshakeAsync(
        HandshakeRequest request,
        [NotBody] MicroserviceContext db,
        [NotBody] IFederationAcceptanceEvaluator evaluator,
        [NotBody] ILogger<FederationHandshakeEndpoint> logger,
        CancellationToken cancellationToken)
    {
        if (!FederationHandshakeService.VerifySignature(request))
        {
            logger.LogWarning("Invalid handshake signature from {Host}", request.Host);
            return (Results.BadRequest("Invalid signature"), null);
        }

        var existing = await db.FederationInstances
            .FirstOrDefaultAsync(i => i.Host == request.Host, cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == FederationStatus.Blocked || existing.Status == FederationStatus.Defederated)
            {
                logger.LogWarning("Rejected handshake from {Status} instance {Host}", existing.Status, request.Host);
                return (Results.StatusCode(403), null);
            }

            var currentStatus = existing.Status == FederationStatus.Active ? "accepted" : "pending";
            return (Results.Ok(HandshakeResponse.ForCurrentInstance(currentStatus)), null);
        }

        var autoAccept = await evaluator.ShouldAutoAcceptAsync(request.Host, cancellationToken);
        var newStatus = autoAccept ? FederationStatus.Active : FederationStatus.Pending;

        var instance = new FederationInstance
        {
            Id = FederationInstance.GenerateId(),
            Host = request.Host,
            Name = request.Name,
            PublicKey = request.PublicKey,
            Status = newStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.FederationInstances.Add(instance);

        var responseStatus = autoAccept ? "accepted" : "pending";
        logger.LogInformation("Handshake from {Host}: {Status}", request.Host, responseStatus);

        var message = new FederationHandshakeReceived(
            request.Host, request.Name, request.PublicKey,
            request.ProtocolVersion, newStatus);

        return (Results.Ok(HandshakeResponse.ForCurrentInstance(responseStatus)), message);
    }
}
