using Federation.Application.Dtos.Events;
using Federation.Application.Providers;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

public class FederationEndpoint
{
    /// <summary>Accepts a signed event from a registered federation partner.</summary>
    /// <param name="http">The request, read as raw bytes so the signature is checked against what arrived.</param>
    /// <param name="provider">The federation provider that resolves the event against the DAG.</param>
    /// <param name="logger">The endpoint logger.</param>
    /// <param name="context">The federation database.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 when the event was accepted, 400 for an unusable body or host, 403 for an inactive instance.</returns>
    [WolverinePost("api/v1/federation/events")]
    public async Task<IResult> EventAsync(
        [NotBody] HttpContext http,
        [NotBody] IFederationProvider provider,
        [NotBody] ILogger<FederationEndpoint> logger,
        [NotBody] MicroserviceContext context,
        CancellationToken cancellationToken)
    {
        // The body is buffered whole because the signature covers the payload's received bytes, and
        // a stream that has already been deserialized cannot produce them again.
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, cancellationToken);

        if (!SignedFederationEvent.TryParse(buffer.ToArray(), out var @event) || @event is null)
        {
            logger.LogWarning("Malformed federation event body");
            return Results.BadRequest();
        }

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
