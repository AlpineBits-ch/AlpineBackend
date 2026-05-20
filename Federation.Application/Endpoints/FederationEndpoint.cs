using System.Text.Json;
using System.Text.Json.Serialization;
using Federation.Application.Dtos.Events;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

public class FederationEndpoint 
{
    [WolverinePost("api/v1/federation/events")]
    public async Task<IResult> EventAsync(FederationEvent @event, [NotBody] ILogger<FederationEndpoint> logger)
    {
        logger.LogInformation("Got event {event}", JsonSerializer.Serialize(@event));
        return Results.Ok();
    }
}