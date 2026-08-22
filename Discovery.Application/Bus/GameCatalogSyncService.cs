using System.Data;
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Social.Contracts.Bus.Integration.Request;
using Wolverine;

namespace Discovery.Api.Bus;

/// <summary>The row counts from one reconciliation pass, for the caller to log.</summary>
public readonly record struct GameCatalogSyncResult(int Inserted, int Updated, int Disabled);

public static class GameCatalogSync
{
    /// <summary>
    /// Mutates the tracked context and returns without saving. The event handler that calls this
    /// runs inside Wolverine's transactional middleware, which commits on a successful return; a
    /// save here would double-commit. The hosted service below owns its own commit instead.
    /// </summary>
    public static async Task<GameCatalogSyncResult> RunAsync(MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
    {
        var existing = await ctx.GameTopics.ToDictionaryAsync(g => g.GameApplicationId, ct);
        var seen = new HashSet<string>();
        var inserted = 0;
        var updated = 0;

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
                    inserted++;
                }
                else
                {
                    updated++;
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
        var disabled = 0;
        foreach (var row in existing.Values.Where(r => !seen.Contains(r.GameApplicationId)))
        {
            if (row.IsEnabled) disabled++;
            row.IsEnabled = false;
        }

        return new GameCatalogSyncResult(inserted, updated, disabled);
    }
}

/// <summary>The retry schedule for <see cref="GameCatalogSyncService"/>. Pulled out as plain functions so a
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

/// <summary>Outcome of one sync attempt. A lock skip is not a failure: it means another replica is
/// doing the work, not that nothing happened.</summary>
public enum GameCatalogSyncOutcome
{
    SkippedLockHeld,
    Applied,
}

/// <summary>
/// Syncs once the host has started, then daily. The event handler covers everything in between. A
/// failed attempt retries on <see cref="GameCatalogSyncSchedule"/>'s short backoff instead of
/// waiting for the next daily tick.
///
/// Discovery runs more than one replica, so every pod's ExecuteAsync fires this on the same cold
/// start. Each takes an advisory lock before writing so only one pod pages the catalog in at a time;
/// the rest skip quietly. Matches the lock GameCatalogSeeder (Social) already ships for the identical
/// race.
/// </summary>
public class GameCatalogSyncService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<GameCatalogSyncService> logger) : BackgroundService
{
    /// <summary>Advisory-lock key.</summary>
    private const long AdvisoryLockKey = 0x47414D45544F5043L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start before Wolverine's runtime does; InvokeAsync throws
        // WolverineHasNotStartedException until ApplicationStarted fires.
        await WaitUntilStartedAsync(lifetime, logger, stoppingToken);

        await RunLoopAsync(SyncOnceAsync, (delay, ct) => Task.Delay(delay, ct), logger, stoppingToken);
    }

    private static async Task WaitUntilStartedAsync(IHostApplicationLifetime lifetime, ILogger logger, CancellationToken stoppingToken)
    {
        logger.LogInformation("Game catalog sync waiting for the host to finish starting");

        if (!lifetime.ApplicationStarted.IsCancellationRequested)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
        }

        logger.LogInformation("Game catalog sync host start wait complete");
    }

    private async Task<GameCatalogSyncOutcome> SyncOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        var opened = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            return await RunOnceWithLockAsync(
                c => TryAcquireLockAsync(connection, c),
                c => ReleaseLockAsync(connection, c),
                async c =>
                {
                    var result = await GameCatalogSync.RunAsync(ctx, bus, c);
                    await ctx.SaveChangesAsync(c);

                    logger.LogInformation(
                        "Game catalog sync applied: {Inserted} inserted, {Updated} updated, {Disabled} disabled",
                        result.Inserted, result.Updated, result.Disabled);
                },
                logger,
                ct);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static async Task<bool> TryAcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        // try_ rather than the blocking form: a pod that cannot have the lock has nothing to wait
        // for, because whoever holds it is doing the same work.
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Acquires the lock, runs the sync, and always releases it. Session-scoped: Postgres frees the
    /// lock when the holding connection closes, including a crash, so a dead pod cannot hold it past
    /// that connection's lifetime. Static and parameterized so a test can fake the lock and the sync
    /// without a live database.
    /// </summary>
    internal static async Task<GameCatalogSyncOutcome> RunOnceWithLockAsync(
        Func<CancellationToken, Task<bool>> tryAcquireLock,
        Func<CancellationToken, Task> releaseLock,
        Func<CancellationToken, Task> sync,
        ILogger logger,
        CancellationToken ct)
    {
        if (!await tryAcquireLock(ct))
        {
            logger.LogInformation("Game catalog sync skipped: advisory lock held by another instance");
            return GameCatalogSyncOutcome.SkippedLockHeld;
        }

        try
        {
            await sync(ct);
            return GameCatalogSyncOutcome.Applied;
        }
        finally
        {
            await releaseLock(ct);
        }
    }

    /// <summary>
    /// The retry loop, static and driven entirely by its parameters so a test can pin the delay
    /// sequence with a fake sync and a fake delay, without a live bus or a real timer. A lock skip
    /// returns normally rather than throwing, so it falls into the same branch as a real success:
    /// the failure streak resets and the next attempt waits for the daily cadence, not the backoff.
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
