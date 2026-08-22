using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using StackExchange.Redis;
using Wolverine;

namespace Discovery.Api.Bus;

/// <summary>The row counts from one reconciliation pass, for the caller to log.</summary>
public readonly record struct GameCatalogSyncResult(int Inserted, int Updated, int Disabled);

public static class GameCatalogSync
{
    /// <summary>
    /// Pages the whole catalog, committing through <paramref name="commit"/> at each page boundary
    /// (ListGameTopicsRequest already pages at 500) instead of accumulating all ~10,400 rows into one
    /// transaction. A failure past a given page's commit has already persisted that page; a resumed
    /// run reloads existing rows fresh and re-pages from the start, so an already-committed row is
    /// just re-upserted rather than duplicated. The disable pass below only runs once every page has
    /// been seen in this same run, so a partial run never disables rows an earlier page just wrote.
    /// Both the hosted service and GameCatalogChangedHandler drive this through
    /// GameCatalogSyncService.SyncOnceAsync, which supplies the real commit.
    /// </summary>
    public static async Task<GameCatalogSyncResult> RunAsync(
        MicroserviceContext ctx, IMessageBus bus, Func<CancellationToken, Task> commit, CancellationToken ct)
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

            // Commits this page before the next page's bus round trip: a crash from here on has
            // already persisted every page committed so far.
            await commit(ct);
        } while (cursor is not null);

        // Disabled, not deleted: a listing already tagged with a game that left the catalogue must
        // keep rendering its chip. Gated behind the full loop above: seen only reflects every page
        // once the loop finishes, so a run that dies partway never reaches this and never wrongly
        // disables a row an earlier page in the same run just wrote.
        var disabled = 0;
        foreach (var row in existing.Values.Where(r => !seen.Contains(r.GameApplicationId)))
        {
            if (row.IsEnabled) disabled++;
            row.IsEnabled = false;
        }

        await commit(ct);

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
/// The lease operations GameCatalogSyncService needs, narrowed from IDatabase so a test can fake it
/// without standing up the rest of the StackExchange.Redis surface.
/// </summary>
internal interface IGameCatalogSyncLeaseStore
{
    /// <summary>SET key value NX PX ttl. False means someone else holds the lease; a connectivity
    /// failure is left to propagate rather than being reported as false.</summary>
    Task<bool> TryAcquireAsync(string key, string token, TimeSpan ttl, CancellationToken ct);

    /// <summary>Deletes the key only if it still holds <paramref name="token"/>. False means this
    /// attempt's lease had already expired and someone else holds it now.</summary>
    Task<bool> ReleaseAsync(string key, string token, CancellationToken ct);

    /// <summary>Resets the TTL only if the key still holds <paramref name="token"/>.</summary>
    Task<bool> ExtendAsync(string key, string token, TimeSpan ttl, CancellationToken ct);
}

/// <summary>
/// Backed by StackExchange.Redis's own lock primitives: LockTakeAsync is SET NX PX, LockReleaseAsync
/// and LockExtendAsync are a Lua compare-value-then-act, so the token check in each is already
/// atomic against a concurrent write from another instance.
/// </summary>
internal sealed class RedisGameCatalogSyncLeaseStore(IDatabase db) : IGameCatalogSyncLeaseStore
{
    public Task<bool> TryAcquireAsync(string key, string token, TimeSpan ttl, CancellationToken ct) =>
        db.LockTakeAsync(key, token, ttl);

    public Task<bool> ReleaseAsync(string key, string token, CancellationToken ct) =>
        db.LockReleaseAsync(key, token);

    public Task<bool> ExtendAsync(string key, string token, TimeSpan ttl, CancellationToken ct) =>
        db.LockExtendAsync(key, token, ttl);
}

/// <summary>
/// Syncs once the host has started, then daily. The event handler covers everything in between. A
/// failed attempt retries on <see cref="GameCatalogSyncSchedule"/>'s short backoff instead of
/// waiting for the next daily tick.
///
/// Discovery runs more than one replica, so every pod's ExecuteAsync fires this on the same cold
/// start. Each takes a Redis lease before writing so only one pod pages the catalog in at a time;
/// the rest skip quietly. This is a Redis lease and not the session-scoped
/// pg_try_advisory_lock/pg_advisory_unlock that GameCatalogSeeder (Social) and
/// ProductCatalogAutoImportService (Guild) use for the identical race: every service here connects
/// through PgBouncer, so the server-side session a session-scoped lock rides on belongs to the
/// pooler, not the pod, and a pod that dies mid-sync leaks the lock until PgBouncer itself is
/// restarted. Do not copy that precedent for a new pooled service.
/// </summary>
public class GameCatalogSyncService(
    IServiceProvider services,
    IConnectionMultiplexer redis,
    IHostApplicationLifetime lifetime,
    ILogger<GameCatalogSyncService> logger) : BackgroundService
{
    private const string LeaseKey = "discovery:game-catalog-sync:lease";

    /// <summary>
    /// A full sync pages roughly 10,400 rows in batches of 500, about 21 request/response round
    /// trips over the bus; each normally completes in well under a second, so two minutes is
    /// comfortably longer than a normal pass. Kept short rather than raised further because the
    /// lease is also renewed below: a stuck pod frees this on the TTL clock, not on the size of this
    /// constant.
    /// </summary>
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(2);

    /// <summary>Well under half the TTL, so one missed renewal never lets the lease lapse.</summary>
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(40);

    /// <summary>
    /// A bulk mirror, not an interactive query: Npgsql's 30s default is sized for the latter. Set on
    /// this sync's own DbContext instance only, never the shared connection string, so nothing else
    /// in the process inherits it.
    /// </summary>
    private const int SyncCommandTimeoutSeconds = 120;

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

    /// <summary>
    /// Acquires the lease, pages and chunk-commits the whole catalog, releases the lease. Internal,
    /// not private: GameCatalogChangedHandler calls this directly too, so an event-triggered sync
    /// gets the same lease guard and per-page commits as the daily one, rather than paging the
    /// catalog itself inside Wolverine's transactional handler.
    /// </summary>
    internal async Task<GameCatalogSyncOutcome> SyncOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        ctx.Database.SetCommandTimeout(SyncCommandTimeoutSeconds);

        var store = new RedisGameCatalogSyncLeaseStore(redis.GetDatabase());
        // Unique per attempt: release (and renewal) below only ever act on the lease this attempt
        // itself took, never one a later pod has since acquired after this one's lease expired.
        var token = Guid.NewGuid().ToString("N");

        return await RunOnceWithLockAsync(
            c => TryAcquireLeaseAsync(store, token, c),
            c => ReleaseLeaseAsync(store, token, logger, c),
            async c =>
            {
                await using var renewal = StartLeaseRenewal(store, token, logger, c);

                var result = await GameCatalogSync.RunAsync(ctx, bus, cc => ctx.SaveChangesAsync(cc), c);

                logger.LogInformation(
                    "Game catalog sync applied: {Inserted} inserted, {Updated} updated, {Disabled} disabled",
                    result.Inserted, result.Updated, result.Disabled);
            },
            logger,
            ct);
    }

    /// <summary>
    /// SET key value NX PX ttl under the hood. A connectivity exception here propagates out of
    /// RunOnceWithLockAsync uncaught, so an unreachable lease store is a failed attempt that goes
    /// through the retry backoff below, never an unguarded sync and never a crash. Internal, not
    /// private, so a test can drive it against a fake store without a live Redis.
    /// </summary>
    internal static Task<bool> TryAcquireLeaseAsync(IGameCatalogSyncLeaseStore store, string token, CancellationToken ct) =>
        store.TryAcquireAsync(LeaseKey, token, LeaseTtl, ct);

    /// <summary>
    /// Deletes the lease only if it still holds the token this attempt took, so an attempt whose
    /// lease already expired cannot delete the lease a later pod has since acquired. Swallows a
    /// store failure here rather than propagating it: the sync itself already succeeded or was
    /// already reported as failed, and the lease is a TTL, so a release that cannot reach the store
    /// still clears on its own.
    /// </summary>
    internal static async Task ReleaseLeaseAsync(IGameCatalogSyncLeaseStore store, string token, ILogger logger, CancellationToken ct)
    {
        try
        {
            var released = await store.ReleaseAsync(LeaseKey, token, ct);
            if (!released)
                logger.LogWarning("Game catalog sync lease had already expired and moved to another instance before release");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game catalog sync could not release its lease; it will expire on its own");
        }
    }

    /// <summary>
    /// Keeps the lease alive for a sync that runs longer than one renewal interval, so the TTL can
    /// stay short instead of being sized for a worst case that mostly never happens. Extending, like
    /// releasing, only takes effect while this attempt's token still holds the lease.
    /// </summary>
    internal static IAsyncDisposable StartLeaseRenewal(IGameCatalogSyncLeaseStore store, string token, ILogger logger, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(LeaseRenewInterval, cts.Token);
                    try
                    {
                        await store.ExtendAsync(LeaseKey, token, LeaseTtl, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Game catalog sync lease renewal failed; the lease may expire before the sync finishes");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Sync finished; the loop is being torn down, not the lease being lost.
            }
        }, ct);

        return new LeaseRenewalHandle(cts, loop);
    }

    private sealed class LeaseRenewalHandle(CancellationTokenSource cts, Task loop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Acquires the lease, runs the sync, and always releases it. Static and parameterized so a test
    /// can fake the lease and the sync without a live Redis.
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
            logger.LogInformation("Game catalog sync skipped: lease held by another instance");
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
