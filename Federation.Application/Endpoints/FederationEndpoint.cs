using System.Text.Json;
using System.Text.Json.Serialization;
using Federation.Application.Dtos.Events;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

public class FederationEndpoint 
{
    [WolverinePost("api/v1/federation/events")]
    public async Task<(IResult, FederationEvent?)> EventAsync([FromBody] SignedFederationEvent @event, [NotBody] ILogger<FederationEndpoint> logger, MicroserviceContext context)
    {
        logger.LogInformation("Got event {event}", JsonSerializer.Serialize(@event));
        
        var federatedSystem = await context.FederationInstances.FirstOrDefaultAsync(i => i.Host == @event.Payload.Host);
        
        if(federatedSystem is null) 
        {
            logger.LogWarning("No federated system found for public key {key}", @event.PublicKey);
            return (Results.BadRequest(), null);
        }
        if (federatedSystem.Status != FederationStatus.Active)
        {
            logger.LogWarning("Federation instance {host} is not active", federatedSystem.Host);
            return (Results.Forbid(), null);
        }
        if (!@event.IsValid(federatedSystem))
        {
            return (Results.BadRequest(), null);
        }
        
        return (Results.Ok(), @event.Payload);
    }
}