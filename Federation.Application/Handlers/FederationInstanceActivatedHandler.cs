using Federation.Application.Messages;

namespace Federation.Application.Handlers;

public class FederationInstanceActivatedHandler
{
    public static Task Handle(
        FederationInstanceActivated message,
        ILogger<FederationInstanceActivatedHandler> logger)
    {
        logger.LogInformation(
            "Federation instance {Host} (id={Id}) is now active",
            message.Host, message.InstanceId);
        return Task.CompletedTask;
    }
}
