using Social.Contracts.Bus.Integration.Events;

namespace Discovery.Api.Bus;

public class GameCatalogChangedHandler
{
    // Delegates to GameCatalogSyncService.SyncOnceAsync rather than calling GameCatalogSync.RunAsync
    // here: that owns its own scope and chunked commits outside Wolverine, so this handler never
    // calls SaveChangesAsync itself and there is nothing left for AutoApplyTransactions to commit.
    public static Task Handle(GameCatalogChanged message, GameCatalogSyncService syncService, CancellationToken ct)
        => syncService.SyncOnceAsync(ct);
}
