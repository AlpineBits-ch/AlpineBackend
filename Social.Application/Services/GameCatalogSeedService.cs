using Social.Infrastructure.Seed;

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
