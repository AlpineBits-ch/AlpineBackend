namespace Guild.Application.Services;

/// <summary>Runs the stale-turn sweep on a timer.</summary>
public class SceneNudgeSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<SceneNudgeSweepService> logger) : BackgroundService
{
    /// <summary>Turns are chased at a 24 hour grace, so the pass only has to be finer than that.
    /// Fifteen minutes matches how promptly a deadline should visibly bite.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var nudges = scope.ServiceProvider.GetRequiredService<SceneNudgeService>();

                await nudges.SendDueNudgesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scene stale-turn sweep failed");
            }
        }
    }
}
