using Federation.Application.Messages;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Federation.Application.Handlers;

public class FederationHandshakeReceivedHandler
{
    public static async Task Handle(
        FederationHandshakeReceived message,
        MicroserviceContext db,
        IMessageBus bus,
        ILogger<FederationHandshakeReceivedHandler> logger)
    {
        if (message.Status == FederationStatus.Active)
        {
            var instance = await db.FederationInstances
                .FirstOrDefaultAsync(i => i.Host == message.Host);

            if (instance is not null)
                await bus.PublishAsync(new FederationInstanceActivated(instance.Id, message.Host));
        }
        else
        {
            logger.LogInformation(
                "Federation instance {Host} is pending approval", message.Host);
        }
    }
}
