using Social.Contracts.Bus.Integration.Events;
using Social.Infrastructure.Seed;
using Wolverine;

namespace Social.Api.Services;

/// <summary>Runs the game-catalog bootstrap seed once the service is already up.</summary>
public sealed class GameCatalogSeedService(
    IServiceScopeFactory scopeFactory,
    ILogger<GameCatalogSeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var seeder = scope.ServiceProvider.GetRequiredService<GameCatalogSeeder>();

            var outcome = await seeder.SeedAsync(stoppingToken);
            logger.LogInformation("Game catalog seed outcome: {Outcome}.", outcome);

            // Only a real apply changes rows Discovery mirrors; AlreadyCurrent and
            // SkippedLockHeld leave the catalog exactly as Discovery last saw it.
            if (outcome == SeedOutcome.Applied)
            {
                // IMessageBus is Wolverine-scoped; this hosted service is a singleton, so it must
                // come from this scope, not the constructor.
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                await bus.PublishAsync(new GameCatalogChanged { Version = GameCatalogSeedReader.Read().Version });
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown during a first seed.
        }
        catch (Exception ex)
        {
            // A catalog that failed to load costs game detection and nothing else.
            logger.LogError(ex, "Game catalog seeding failed; game detection will be unavailable until the next restart.");
        }
    }
}
