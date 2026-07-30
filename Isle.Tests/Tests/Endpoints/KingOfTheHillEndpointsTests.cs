using Isle.Api.Endpoints;
using Isle.Api.Services.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Interfaces;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers KingOfTheHillEndpoints - a static, Wolverine-attributed HTTP surface, called here as
/// plain C# methods with a TestIsleContext and Redis-backed KOTH services (see
/// KingOfTheHillDirectorTests/KingOfTheHillTickServiceTests for the same construction pattern).
/// </summary>
[TestFixture]
public class KingOfTheHillEndpointsTests
{
    private TestIsleContext _context = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private KingOfTheHillControlLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(), NullLogger<KingOfTheHillMatchStateStore>.Instance);
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── ActiveMatch ───────────────────────────────────────────────────────

    [Test]
    public async Task ActiveMatch_NoMatchRunning_ReturnsNull()
    {
        var result = await KingOfTheHillEndpoints.ActiveMatch(_stateStore, _ledger, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ActiveMatch_MatchRunning_ReturnsMarkerWithStandings()
    {
        var startedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await _stateStore.WriteAsync(new KothMatchState("def-1", "instance-1", startedAt, ["steam_1"]));
        await _ledger.ApplyPresenceAsync("instance-1", ["steam_1"]);

        var result = await KingOfTheHillEndpoints.ActiveMatch(_stateStore, _ledger, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.InstanceId, Is.EqualTo("instance-1"));
        Assert.That(result.StartedAt, Is.EqualTo(startedAt));
        Assert.That(result.Standings, Has.Count.EqualTo(1));
        Assert.That(result.Standings[0].SteamId, Is.EqualTo("steam_1"));
        Assert.That(result.Standings[0].Ticks, Is.EqualTo(1));
    }

    [Test]
    public async Task ActiveMatch_MarkerExistsButNobodyEverCredited_ReturnsEmptyStandings()
    {
        await _stateStore.WriteAsync(new KothMatchState("def-1", "instance-2", DateTime.UtcNow, []));

        var result = await KingOfTheHillEndpoints.ActiveMatch(_stateStore, _ledger, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Standings, Is.Empty);
    }

    // ── History ───────────────────────────────────────────────────────────

    [Test]
    public async Task History_NoRuns_ReturnsEmpty()
    {
        var result = await KingOfTheHillEndpoints.History(_context, CancellationToken.None);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task History_OnlyIncludesRunsOfTheKothDefinition_OrderedNewestFirst()
    {
        var kothDefinition = TestData.KothDefinition();
        var otherDefinition = TestData.KothDefinition();
        otherDefinition.DisplayName = "Some Future Mode";
        _context.GameModeDefinitions.AddRange(kothDefinition, otherDefinition);
        await _context.SaveChangesAsync();

        var olderRun = MakeRun(kothDefinition, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        var newerRun = MakeRun(kothDefinition, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-10));
        var otherModeRun = MakeRun(otherDefinition, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
        _context.GameModeRuns.AddRange(olderRun, newerRun, otherModeRun);
        await _context.SaveChangesAsync();

        var result = await KingOfTheHillEndpoints.History(_context, CancellationToken.None);

        Assert.That(result.Select(r => r.InstanceId), Is.EqualTo(new[] { newerRun.Id, olderRun.Id }));
    }

    [Test]
    public async Task History_MapsResultsOntoTheDto()
    {
        var definition = TestData.KothDefinition();
        _context.GameModeDefinitions.Add(definition);
        await _context.SaveChangesAsync();

        var instance = new GameModeInstance(definition, Substitute.For<IGameMode>());
        instance.Start();
        var run = new GameModeRun(instance, [new ParticipantStanding { PlayerId = "player_1", Score = 10, Rank = 1, CustomMetrics = new() }]);
        _context.GameModeRuns.Add(run);
        await _context.SaveChangesAsync();

        var result = await KingOfTheHillEndpoints.History(_context, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Results.Single().PlayerId, Is.EqualTo("player_1"));
        Assert.That(result[0].Results.Single().Score, Is.EqualTo(10));
    }

    private static GameModeRun MakeRun(GameModeDefinition definition, DateTime startedAt, DateTime endedAt)
    {
        var instance = GameModeInstance.Resume(definition, Substitute.For<IGameMode>(), GameModeInstance.GenerateId(), startedAt, []);
        return new GameModeRun(instance, []) { }.With(endedAt);
    }
}

file static class GameModeRunTestExtensions
{
    /// <summary>
    /// GameModeRun's constructor always stamps EndedAt as DateTime.UtcNow, which makes ordering
    /// fixtures created back-to-back indistinguishable in a fast test - this pins it to an explicit
    /// value via reflection instead of adding a test-only constructor overload to production code.
    /// </summary>
    public static GameModeRun With(this GameModeRun run, DateTime endedAt)
    {
        typeof(GameModeRun)
            .GetProperty(nameof(GameModeRun.EndedAt), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(run, endedAt);
        return run;
    }
}
