using Discovery.Api.Bus;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;

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

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

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

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

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

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var gone = ctx.GameTopics.Single(g => g.GameApplicationId == "gapp_gone");
        Assert.That(gone.IsEnabled, Is.False);
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
