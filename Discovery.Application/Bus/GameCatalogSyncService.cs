using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Wolverine;

namespace Discovery.Api.Bus;

public static class GameCatalogSync
{
    /// <summary>
    /// Mutates the tracked context and returns without saving. The event handler that calls this
    /// runs inside Wolverine's transactional middleware, which commits on a successful return; a
    /// save here would double-commit. The hosted service below owns its own commit instead.
    /// </summary>
    public static async Task RunAsync(MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
    {
        var existing = await ctx.GameTopics.ToDictionaryAsync(g => g.GameApplicationId, ct);
        var seen = new HashSet<string>();

        string? cursor = null;
        do
        {
            var page = await bus.InvokeAsync<ListGameTopicsResponse>(
                new ListGameTopicsRequest { After = cursor }, ct);

            foreach (var dto in page.Topics)
            {
                seen.Add(dto.Id);
                if (!existing.TryGetValue(dto.Id, out var row))
                {
                    row = new GameTopic { Id = GameTopic.GenerateId(), GameApplicationId = dto.Id };
                    ctx.GameTopics.Add(row);
                    existing[dto.Id] = row;
                }

                row.Name = dto.Name;
                row.Aliases = dto.Aliases;
                row.SteamAppId = dto.SteamAppId;
                row.IsEnabled = dto.IsEnabled;
                // Name plus every alias, lower-invariant - see GameTopic.SearchText. Set here on
                // both the insert and the update path so a rename or a new alias list is not stale.
                row.SearchText = string.Join(' ', new[] { dto.Name }.Concat(dto.Aliases)).ToLowerInvariant();
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        // Disabled, not deleted: a listing already tagged with a game that left the catalogue must
        // keep rendering its chip.
        foreach (var row in existing.Values.Where(r => !seen.Contains(r.GameApplicationId)))
            row.IsEnabled = false;
    }
}

/// <summary>
/// The retry schedule for <see cref="GameCatalogSyncService"/>. Pulled out as plain functions so a
/// test can pin the delay sequence without waiting on any of them.
/// </summary>
internal static class GameCatalogSyncSchedule
{
    public static readonly TimeSpan SuccessInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan[] FailureBackoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>Escalate to ERROR once a streak reaches this length: by the fifth failure (about 18
    /// minutes of backoff elapsed) an empty mirror stops being a blip a WARNING can carry alone.</summary>
    public const int ConsecutiveFailuresBeforeError = 5;

    /// <summary>attempt counts from 1. Stays on the longest step rather than growing past it.</summary>
    public static TimeSpan DelayAfterFailure(int attempt) =>
        FailureBackoff[Math.Min(Math.Max(attempt, 1), FailureBackoff.Length) - 1];
}

/// <summary>
/// Syncs once the host has started, then daily. The event handler covers everything in between. A
/// failed attempt retries on <see cref="GameCatalogSyncSchedule"/>'s short backoff instead of
/// waiting for the next daily tick.
/// </summary>
public class GameCatalogSyncService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<GameCatalogSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start before Wolverine's runtime does; InvokeAsync throws
        // WolverineHasNotStartedException until ApplicationStarted fires.
        await WaitUntilStartedAsync(lifetime, stoppingToken);

        await RunLoopAsync(SyncOnceAsync, (delay, ct) => Task.Delay(delay, ct), logger, stoppingToken);
    }

    private static async Task WaitUntilStartedAsync(IHostApplicationLifetime lifetime, CancellationToken stoppingToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        await GameCatalogSync.RunAsync(ctx, scope.ServiceProvider.GetRequiredService<IMessageBus>(), ct);
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The retry loop, static and driven entirely by its parameters so a test can pin the delay
    /// sequence with a fake sync and a fake delay, without a live bus or a real timer.
    /// </summary>
    internal static async Task RunLoopAsync(
        Func<CancellationToken, Task> syncOnce,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await syncOnce(stoppingToken);
                consecutiveFailures = 0;
                await delay(GameCatalogSyncSchedule.SuccessInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var nextDelay = GameCatalogSyncSchedule.DelayAfterFailure(consecutiveFailures);

                logger.LogWarning(ex,
                    "Game catalog sync failed on attempt {Attempt}, retrying in {Delay}",
                    consecutiveFailures, nextDelay);

                if (consecutiveFailures >= GameCatalogSyncSchedule.ConsecutiveFailuresBeforeError)
                {
                    logger.LogError(
                        "Game catalog sync has failed {Attempt} times in a row; the mirror may "
                        + "still be empty and topic search is returning no results with no other trace",
                        consecutiveFailures);
                }

                await delay(nextDelay, stoppingToken);
            }
        }
    }
}
