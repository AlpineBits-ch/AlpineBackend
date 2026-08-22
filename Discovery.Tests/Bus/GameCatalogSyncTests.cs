using Discovery.Api.Bus;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using StackExchange.Redis;

namespace Discovery.Tests.Bus;

[TestFixture]
public class GameCatalogSyncTests
{
    private static ListGameTopicsResponse Page(string? next, params GameTopicDto[] topics) =>
        new() { Topics = topics, NextCursor = next };

    private static GameTopicDto Game(string id, string name, bool isEnabled = true) =>
        new() { Id = id, Name = name, IsEnabled = isEnabled };

    [Test]
    public async Task A_first_sync_writes_every_page()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(request =>
            request.After is null
                ? Page(next: "gapp_2", Game("gapp_1", "The Isle"))
                : Page(next: null, Game("gapp_2", "MSFS 2024")));

        await GameCatalogSync.RunAsync(ctx, bus, c => ctx.SaveChangesAsync(c), CancellationToken.None);

        Assert.That(ctx.GameTopics.Select(g => g.Name), Is.EquivalentTo(new[] {"The Isle", "MSFS 2024"}));
    }

    [Test]
    public async Task A_resync_updates_a_renamed_game_rather_than_duplicating_it()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_1", GameApplicationId = "gapp_1", Name = "Old"});
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(_ =>
            Page(next: null, Game("gapp_1", "New")));

        await GameCatalogSync.RunAsync(ctx, bus, c => ctx.SaveChangesAsync(c), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.GameTopics.Count(), Is.EqualTo(1));
            Assert.That(ctx.GameTopics.Single().Name, Is.EqualTo("New"));
        });
    }

    [Test]
    public async Task A_game_that_left_the_catalogue_stops_being_offered()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_1", GameApplicationId = "gapp_gone", Name = "Gone", IsEnabled = true});
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(_ =>
            Page(next: null, Game("gapp_1", "Here")));

        await GameCatalogSync.RunAsync(ctx, bus, c => ctx.SaveChangesAsync(c), CancellationToken.None);

        var gone = ctx.GameTopics.Single(g => g.GameApplicationId == "gapp_gone");
        Assert.That(gone.IsEnabled, Is.False);
    }

    [Test]
    public async Task A_sync_reports_how_many_rows_it_inserted_updated_and_disabled()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_1", GameApplicationId = "gapp_stay", Name = "Stay"});
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_2", GameApplicationId = "gapp_gone", Name = "Gone", IsEnabled = true});
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(_ =>
            Page(next: null, Game("gapp_stay", "Stay"), Game("gapp_new", "New")));

        var result = await GameCatalogSync.RunAsync(ctx, bus, c => ctx.SaveChangesAsync(c), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Inserted, Is.EqualTo(1));
            Assert.That(result.Updated, Is.EqualTo(1));
            Assert.That(result.Disabled, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task The_page_boundary_is_the_commit_boundary_not_the_whole_run()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(request =>
            request.After switch
            {
                null => Page(next: "gapp_2", Game("gapp_1", "The Isle")),
                "gapp_2" => Page(next: "gapp_3", Game("gapp_2", "MSFS 2024")),
                _ => Page(next: null, Game("gapp_3", "Flight Sim World")),
            });

        var commits = 0;
        Task Commit(CancellationToken c)
        {
            commits++;
            return ctx.SaveChangesAsync(c);
        }

        await GameCatalogSync.RunAsync(ctx, bus, Commit, CancellationToken.None);

        // 3 pages plus the trailing disable-pass commit; never one commit for the whole run.
        Assert.That(commits, Is.EqualTo(4));
    }

    [Test]
    public async Task A_run_that_fails_partway_has_still_persisted_the_pages_it_completed()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(request =>
            request.After is null
                ? Page(next: "gapp_2", Game("gapp_1", "The Isle"))
                : Page(next: null, Game("gapp_2", "MSFS 2024")));

        var commits = 0;
        Task Commit(CancellationToken c)
        {
            commits++;
            // Simulates the second page's commit hitting the production failure: a command timeout
            // surfacing from SaveChangesAsync, whatever its exact exception type.
            if (commits == 2) throw new TimeoutException("simulated command timeout on page two");
            return ctx.SaveChangesAsync(c);
        }

        Assert.ThrowsAsync<TimeoutException>(() =>
            GameCatalogSync.RunAsync(ctx, bus, Commit, CancellationToken.None));

        Assert.That(ctx.GameTopics.Select(g => g.Name), Is.EquivalentTo(new[] {"The Isle"}));
    }

    [Test]
    public async Task A_resumed_run_after_a_partial_failure_converges_including_the_disable_pass()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var seed = new TestDiscoveryContext(dbName))
        {
            seed.GameTopics.Add(new GameTopic {Id = "gmtp_gone", GameApplicationId = "gapp_gone", Name = "Gone", IsEnabled = true});
            await seed.SaveChangesAsync();
        }

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(request =>
            request.After is null
                ? Page(next: "gapp_2", Game("gapp_1", "The Isle"))
                : Page(next: null, Game("gapp_2", "MSFS 2024")));

        // First attempt: a fresh scope, page one commits, page two's commit fails partway.
        await using (var ctx = new TestDiscoveryContext(dbName))
        {
            var commits = 0;
            Task Commit(CancellationToken c)
            {
                commits++;
                if (commits == 2) throw new TimeoutException("simulated command timeout on page two");
                return ctx.SaveChangesAsync(c);
            }

            Assert.ThrowsAsync<TimeoutException>(() =>
                GameCatalogSync.RunAsync(ctx, bus, Commit, CancellationToken.None));
        }

        // Resumed attempt: another fresh scope over the same store, no injected failure this time.
        await using (var ctx = new TestDiscoveryContext(dbName))
        {
            var result = await GameCatalogSync.RunAsync(ctx, bus, c => ctx.SaveChangesAsync(c), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(ctx.GameTopics.Select(g => g.Name), Is.EquivalentTo(new[] {"The Isle", "MSFS 2024", "Gone"}));
                Assert.That(ctx.GameTopics.Single(g => g.GameApplicationId == "gapp_gone").IsEnabled, Is.False);
                // gapp_1 was already persisted by the failed first attempt: this run updates it, not inserts it.
                Assert.That(result.Inserted, Is.EqualTo(1));
                Assert.That(result.Updated, Is.EqualTo(1));
                Assert.That(result.Disabled, Is.EqualTo(1));
            });
        }
    }
}

[TestFixture]
public class GameCatalogSyncServiceRetryLoopTests
{
    /// <summary>
    /// Drives GameCatalogSyncService.RunLoopAsync with a fake sync and a fake delay: no real bus,
    /// no real timer. The fake delay records what it was asked to wait and cancels the loop once it
    /// has seen enough calls, instead of a real cancellation racing a real sleep.
    /// </summary>
    private static async Task<List<TimeSpan>> RunAsync(Func<int, bool> failsOnAttempt, int stopAfterCalls)
    {
        var delays = new List<TimeSpan>();
        var cts = new CancellationTokenSource();
        var attempt = 0;

        Task SyncOnce(CancellationToken _)
        {
            attempt++;
            if (failsOnAttempt(attempt)) throw new InvalidOperationException("sync failed");
            return Task.CompletedTask;
        }

        Task Delay(TimeSpan requested, CancellationToken _)
        {
            delays.Add(requested);
            if (delays.Count >= stopAfterCalls) cts.Cancel();
            return Task.CompletedTask;
        }

        await GameCatalogSyncService.RunLoopAsync(SyncOnce, Delay, NullLogger.Instance, cts.Token);
        return delays;
    }

    [Test]
    public async Task A_failed_sync_retries_on_the_short_backoff_not_the_daily_interval()
    {
        var delays = await RunAsync(failsOnAttempt: _ => true, stopAfterCalls: 2);

        Assert.That(delays, Is.EqualTo(new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
        }));
    }

    [Test]
    public async Task A_success_returns_to_the_daily_cadence_and_a_later_failure_restarts_the_backoff()
    {
        // Attempts 1 and 2 fail, 3 succeeds, 4 fails again.
        var delays = await RunAsync(failsOnAttempt: n => n != 3, stopAfterCalls: 4);

        Assert.That(delays, Is.EqualTo(new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(30),
        }));
    }
}

/// <summary>
/// Hand-rolled fake of the Redis lease store: a compare-and-swap slot keyed by an owner token,
/// standing in for SET key value NX PX ttl and its compare-and-delete release/extend. No mocking
/// framework exists in this repo, and a real Redis cannot be exercised without a live instance.
/// </summary>
internal sealed class FakeLeaseStore : IGameCatalogSyncLeaseStore
{
    private readonly object gate = new();
    private string? owner;

    /// <summary>Simulates Redis being unreachable: acquire throws instead of returning false, the
    /// same shape a RedisConnectionException takes in production.</summary>
    public bool ThrowOnAcquire { get; init; }

    public Task<bool> TryAcquireAsync(string key, string token, TimeSpan ttl, CancellationToken ct)
    {
        if (ThrowOnAcquire)
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "lease store unreachable");

        lock (gate)
        {
            if (owner is not null) return Task.FromResult(false);
            owner = token;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(string key, string token, CancellationToken ct)
    {
        lock (gate)
        {
            if (owner != token) return Task.FromResult(false);
            owner = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ExtendAsync(string key, string token, TimeSpan ttl, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(owner == token);
        }
    }

    public bool IsHeldBy(string token)
    {
        lock (gate) return owner == token;
    }

    public bool IsFree
    {
        get { lock (gate) return owner is null; }
    }
}

[TestFixture]
public class GameCatalogSyncServiceLockTests
{
    private static Task<GameCatalogSyncOutcome> AttemptAsync(
        FakeLeaseStore store, Func<CancellationToken, Task> sync, CancellationToken ct = default)
    {
        var token = Guid.NewGuid().ToString("N");
        return GameCatalogSyncService.RunOnceWithLockAsync(
            c => GameCatalogSyncService.TryAcquireLeaseAsync(store, token, c),
            c => GameCatalogSyncService.ReleaseLeaseAsync(store, token, NullLogger.Instance, c),
            sync,
            NullLogger.Instance,
            ct);
    }

    [Test]
    public async Task A_second_concurrent_runner_skips_without_throwing_or_running_the_sync_twice()
    {
        var store = new FakeLeaseStore();
        var syncCount = 0;

        var first = AttemptAsync(store, async _ =>
        {
            Interlocked.Increment(ref syncCount);
            await Task.Delay(20);
        });
        var second = AttemptAsync(store, async _ =>
        {
            Interlocked.Increment(ref syncCount);
            await Task.Delay(20);
        });

        var results = await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.One.EqualTo(GameCatalogSyncOutcome.Applied));
            Assert.That(results, Has.One.EqualTo(GameCatalogSyncOutcome.SkippedLockHeld));
            Assert.That(syncCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_failed_sync_still_releases_the_lease()
    {
        var store = new FakeLeaseStore();

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            AttemptAsync(store, _ => throw new InvalidOperationException("sync failed")));

        Assert.That(store.IsFree, Is.True);
    }

    [Test]
    public async Task A_lock_skip_does_not_trigger_the_failure_backoff_or_the_error_escalation()
    {
        // Someone else holds the lease for the whole test; this replica never gets to sync.
        var store = new FakeLeaseStore();
        var holderToken = Guid.NewGuid().ToString("N");
        Assert.That(await GameCatalogSyncService.TryAcquireLeaseAsync(store, holderToken, CancellationToken.None), Is.True);

        var delays = new List<TimeSpan>();
        var cts = new CancellationTokenSource();

        Task SyncOnce(CancellationToken ct) => AttemptAsync(store, _ => Task.CompletedTask, ct);

        Task Delay(TimeSpan requested, CancellationToken _)
        {
            delays.Add(requested);
            if (delays.Count >= 3) cts.Cancel();
            return Task.CompletedTask;
        }

        await GameCatalogSyncService.RunLoopAsync(SyncOnce, Delay, NullLogger.Instance, cts.Token);

        Assert.That(delays, Is.EqualTo(new[]
        {
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
        }));
    }

    [Test]
    public async Task A_release_does_not_delete_a_lease_owned_by_someone_else()
    {
        var store = new FakeLeaseStore();
        var ownerToken = Guid.NewGuid().ToString("N");
        var staleToken = Guid.NewGuid().ToString("N");

        Assert.That(await GameCatalogSyncService.TryAcquireLeaseAsync(store, ownerToken, CancellationToken.None), Is.True);

        // Simulates an attempt whose own lease already expired: it still tries to release the token
        // it originally took, which must not be the token currently holding the lease.
        await GameCatalogSyncService.ReleaseLeaseAsync(store, staleToken, NullLogger.Instance, CancellationToken.None);

        Assert.That(store.IsHeldBy(ownerToken), Is.True);
    }

    [Test]
    public async Task An_unreachable_lease_store_is_a_failed_attempt_not_an_unguarded_sync()
    {
        var store = new FakeLeaseStore { ThrowOnAcquire = true };
        var syncRan = false;

        Task SyncOnce(CancellationToken ct) => AttemptAsync(store, _ =>
        {
            syncRan = true;
            return Task.CompletedTask;
        }, ct);

        var delays = new List<TimeSpan>();
        var cts = new CancellationTokenSource();

        Task Delay(TimeSpan requested, CancellationToken _)
        {
            delays.Add(requested);
            if (delays.Count >= 2) cts.Cancel();
            return Task.CompletedTask;
        }

        await GameCatalogSyncService.RunLoopAsync(SyncOnce, Delay, NullLogger.Instance, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(syncRan, Is.False);
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1) }));
        });
    }
}
