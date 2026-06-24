using Federation.Application.Dtos.Events;
using Federation.Application.Providers;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

public class FederationEndpoint
{
    [WolverinePost("api/v1/federation/events")]
    public async Task<IResult> EventAsync(
        [FromBody] SignedFederationEvent @event,
        [NotBody] IFederationProvider provider,
        [NotBody] ILogger<FederationEndpoint> logger,
        MicroserviceContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Received federation event {EventType} from {Host}", @event.Payload.GetType().Name, @event.Payload.Host);

        var federatedSystem = await context.FederationInstances.FirstOrDefaultAsync(i => i.Host == @event.Payload.Host, cancellationToken);

        if (federatedSystem is null)
        {
            logger.LogWarning("No federated system found for host {Host}", @event.Payload.Host);
            return Results.BadRequest();
        }
        if (federatedSystem.Status != FederationStatus.Active)
        {
            logger.LogWarning("Federation instance {Host} is not active (status: {Status})", federatedSystem.Host, federatedSystem.Status);
            return Results.Forbid();
        }
        if (!@event.IsValid(federatedSystem))
        {
            logger.LogWarning("Invalid signature on federation event {EventType} from {Host}", @event.Payload.GetType().Name, @event.Payload.Host);
            return Results.BadRequest();
        }

        await provider.HandleInboundEventAsync(@event.Payload, cancellationToken);

        logger.LogInformation("Processed federation event {EventType} from {Host}", @event.Payload.GetType().Name, @event.Payload.Host);
        return Results.Ok();
    }
}
