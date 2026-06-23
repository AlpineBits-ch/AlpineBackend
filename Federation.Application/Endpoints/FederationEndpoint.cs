using System.Text.Json;
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
        logger.LogInformation("Got event {event}", JsonSerializer.Serialize(@event));

        var federatedSystem = await context.FederationInstances.FirstOrDefaultAsync(i => i.Host == @event.Payload.Host, cancellationToken);

        if (federatedSystem is null)
        {
            logger.LogWarning("No federated system found for host {host}", @event.Payload.Host);
            return Results.BadRequest();
        }
        if (federatedSystem.Status != FederationStatus.Active)
        {
            logger.LogWarning("Federation instance {host} is not active", federatedSystem.Host);
            return Results.Forbid();
        }
        if (!@event.IsValid(federatedSystem))
        {
            return Results.BadRequest();
        }

        await provider.HandleInboundEventAsync(@event.Payload, cancellationToken);

        return Results.Ok();
    }
}
