using Discovery.Infrastructure.Persistence;
using Social.Contracts.Bus.Integration.Events;
using Wolverine;

namespace Discovery.Api.Bus;

public class GameCatalogChangedHandler
{
    // No SaveChangesAsync here: Wolverine's AutoApplyTransactions policy commits on a successful
    // return, and GameCatalogSync.RunAsync deliberately leaves the commit to its caller.
    public static Task Handle(GameCatalogChanged message, MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
        => GameCatalogSync.RunAsync(ctx, bus, ct);
}
