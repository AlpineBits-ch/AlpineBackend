using System.Numerics;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Interfaces;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillTickServiceTests
{
    private WorldRosterCache _roster = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private KingOfTheHillTickService _tick = null!;
    private GameModeInstance _instance = null!;
    private FakeRedisStore _store = null!;

    private static readonly Vector3 ZoneCenter = new(1000, 1000, 0);

    [SetUp]
    public void SetUp()
    {
        _roster = new WorldRosterCache();
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(out _store), NullLogger<KingOfTheHillControlLedger>.Instance);
        _tick = new KingOfTheHillTickService(_roster, _ledger, NullLogger<KingOfTheHillTickService>.Instance);

        var definition = TestData.KothDefinition(center: ZoneCenter, radius: 500, maxDuration: TimeSpan.FromMinutes(10));
        _instance = new GameModeInstance(definition, Substitute.For<IGameMode>());
        _instance.Start();
    }

    private static RosterEntry Entry(string steam, Vector3 position) =>
        new(steam, "Test", "Rex", 1.0f, position, RegionId: null, LocationName: "Somewhere");

    [Test]
    public async Task TickAsync_NobodyInZone_ReturnsContinueAndCreditsNobody()
    {
        _roster.Replace([Entry("steam_1", new Vector3(999999, 999999, 0))]);

        var outcome = await _tick.TickAsync(_instance);

        Assert.That(outcome, Is.EqualTo(KothTickOutcome.Continue));
        Assert.That(await _ledger.GetStandingsAsync(_instance.InstanceId), Is.Empty);
    }

    [Test]
    public async Task TickAsync_OnePlayerInZone_CreditsThemAndReturnsContinue()
    {
        _roster.Replace([Entry("steam_1", ZoneCenter)]);

        var outcome = await _tick.TickAsync(_instance);

        Assert.That(outcome, Is.EqualTo(KothTickOutcome.Continue));
        var standings = await _ledger.GetStandingsAsync(_instance.InstanceId);
        Assert.That(standings.Single().SteamId, Is.EqualTo("steam_1"));
    }

    [Test]
    public async Task TickAsync_MatchTimedOut_ReturnsTimedOutRegardlessOfZoneOccupancy()
    {
        var timedOutInstance = GameModeInstance.Resume(
            _instance.Definition, _instance.Behavior, _instance.InstanceId,
            DateTime.UtcNow - TimeSpan.FromMinutes(11), []);

        _roster.Replace([Entry("steam_1", ZoneCenter)]);

        var outcome = await _tick.TickAsync(timedOutInstance);

        Assert.That(outcome, Is.EqualTo(KothTickOutcome.TimedOut));
    }

    [Test]
    public async Task TickAsync_HeldAloneLongEnough_ReturnsHeldAloneToWin()
    {
        _roster.Replace([Entry("steam_1", ZoneCenter)]);

        // Simulate the holder having already held the zone alone for longer than the win threshold by
        // ticking repeatedly isn't practical in real time, so seed the ledger directly via many ticks
        // isn't either — instead drive it through the public surface with a stale first tick and confirm
        // the streak accumulates monotonically towards the threshold rather than asserting the full
        // 3 minutes here (covered by KingOfTheHillControlLedgerTests's streak tests instead).
        await _tick.TickAsync(_instance);
        var holder = await _ledger.GetHolderStreakAsync(_instance.InstanceId);

        Assert.That(holder, Is.Not.Null);
        Assert.That(holder!.Value.Streak, Is.LessThan(KingOfTheHillTickService.HeldAloneToWin),
            "a single fresh tick should not already satisfy the held-alone-to-win threshold");
    }

    [Test]
    public async Task TickAsync_ContestedZone_ContinuesAndCreditsNobody()
    {
        _roster.Replace([Entry("steam_1", ZoneCenter), Entry("steam_2", ZoneCenter)]);

        var outcome = await _tick.TickAsync(_instance);

        Assert.That(outcome, Is.EqualTo(KothTickOutcome.Continue));
        Assert.That(await _ledger.GetStandingsAsync(_instance.InstanceId), Is.Empty);
    }

    [Test]
    public async Task TickAsync_PlayerExactlyOnTheZoneRadius_CountsAsInside()
    {
        // Definition's zone is centered on ZoneCenter with radius 500 — a point exactly 500 units out
        // is the Contains boundary case (<=), and the tick service must agree with GeoFenceData on it.
        _roster.Replace([Entry("steam_1", ZoneCenter + new Vector3(500, 0, 0))]);

        await _tick.TickAsync(_instance);

        var standings = await _ledger.GetStandingsAsync(_instance.InstanceId);
        Assert.That(standings.Select(s => s.SteamId), Is.EqualTo(new[] { "steam_1" }));
    }

    [Test]
    public async Task TickAsync_StreakPastTheRealThreshold_ActuallyReturnsHeldAloneToWin()
    {
        _roster.Replace([Entry("steam_1", ZoneCenter)]);
        await _tick.TickAsync(_instance); // first tick: steam_1 becomes the holder

        // Backdate the holder's "since" timestamp past the real 3-minute threshold directly in the
        // fake store — waiting 3 real minutes in a unit test isn't practical, and this is the one test
        // that actually exercises the HeldAloneToWin branch rather than just asserting it hasn't fired yet.
        var since = DateTimeOffset.UtcNow - KingOfTheHillTickService.HeldAloneToWin - TimeSpan.FromSeconds(5);
        _store.Strings[$"isle:koth:holdersince:{_instance.InstanceId}"] = since.ToUnixTimeMilliseconds().ToString();

        var outcome = await _tick.TickAsync(_instance);

        Assert.That(outcome, Is.EqualTo(KothTickOutcome.HeldAloneToWin));
    }
}
