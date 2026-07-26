using Isle.Api.Services.Quests;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// The automatic quest giver's clock. Each tick closes anything that ran out of time, then asks
/// <see cref="QuestDirector"/> whether to place a new quest — which is where population spreading
/// happens, since the director scores candidate locations against where the crowd currently is.
///
/// <para>Bounty upkeep rides on the same tick rather than a second loop: both are quest-instance
/// lifecycle work on the same DB scope, and a bounty expiring a couple of minutes late is harmless.</para>
/// </summary>
public sealed class QuestDirectorService(
    IServiceScopeFactory scopeFactory,
    ILogger<QuestDirectorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    /// <summary>Let the roster service land its first read before trying to place anything.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await SafeTickAsync(ct);

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var spawner = scope.ServiceProvider.GetRequiredService<QuestSpawner>();
            var director = scope.ServiceProvider.GetRequiredService<QuestDirector>();
            var bounties = scope.ServiceProvider.GetRequiredService<BountyService>();

            await spawner.ExpireDueQuestsAsync(ct);
            await bounties.ExpireDueBountiesAsync(ct);

            if (await director.ChooseAsync(ct) is { } candidate)
                await spawner.SpawnAsync(candidate, adminSpawned: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quest director tick failed");
        }
    }
}
