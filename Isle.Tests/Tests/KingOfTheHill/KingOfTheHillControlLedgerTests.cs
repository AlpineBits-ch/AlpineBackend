using Isle.Api.Services.KingOfTheHill;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillControlLedgerTests
{
    private KingOfTheHillControlLedger _ledger = null!;
    private FakeRedisStore _store = null!;
    private const string InstanceId = "game_instance_test";

    [SetUp]
    public void SetUp()
    {
        var redis = RedisTestFactory.Create(out _store);
        _ledger = new KingOfTheHillControlLedger(redis, NullLogger<KingOfTheHillControlLedger>.Instance);
    }

    [Test]
    public async Task ApplyPresence_SinglePlayer_CreditsOneTickAndBecomesHolder()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);

        var standings = await _ledger.GetStandingsAsync(InstanceId);
        Assert.That(standings, Has.Count.EqualTo(1));
        Assert.That(standings[0].SteamId, Is.EqualTo("steam_1"));
        Assert.That(standings[0].Ticks, Is.EqualTo(1));

        var holder = await _ledger.GetHolderStreakAsync(InstanceId);
        Assert.That(holder, Is.Not.Null);
        Assert.That(holder!.Value.SteamId, Is.EqualTo("steam_1"));
    }

    [Test]
    public async Task ApplyPresence_SamePlayerAcrossTicks_AccumulatesTicksAndKeepsStreak()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        var firstHolder = await _ledger.GetHolderStreakAsync(InstanceId);

        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        var laterHolder = await _ledger.GetHolderStreakAsync(InstanceId);

        var standings = await _ledger.GetStandingsAsync(InstanceId);
        Assert.That(standings[0].Ticks, Is.EqualTo(3));

        // The streak keeps running from the same "since" — it must not have reset on ticks 2 and 3.
        Assert.That(laterHolder!.Value.Streak, Is.GreaterThanOrEqualTo(firstHolder!.Value.Streak));
    }

    [Test]
    public async Task ApplyPresence_EmptyZone_ClearsHolderWithoutTouchingTotals()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);

        await _ledger.ApplyPresenceAsync(InstanceId, []);

        Assert.That(await _ledger.GetHolderStreakAsync(InstanceId), Is.Null);
        var standings = await _ledger.GetStandingsAsync(InstanceId);
        Assert.That(standings[0].Ticks, Is.EqualTo(1), "the earlier credited tick must survive an empty tick");
    }

    [Test]
    public async Task ApplyPresence_ContestedZone_CreditsNobodyAndClearsHolder()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);

        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1", "steam_2"]);

        Assert.That(await _ledger.GetHolderStreakAsync(InstanceId), Is.Null);
        var standings = await _ledger.GetStandingsAsync(InstanceId);
        Assert.That(standings, Has.Count.EqualTo(1), "contested ticks credit neither player");
        Assert.That(standings[0].Ticks, Is.EqualTo(1));
    }

    [Test]
    public async Task ApplyPresence_DifferentSoleHolder_ResetsTheHeldAloneStreak()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        await Task.Delay(10);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_2"]);

        var holder = await _ledger.GetHolderStreakAsync(InstanceId);
        Assert.That(holder!.Value.SteamId, Is.EqualTo("steam_2"));
        Assert.That(holder.Value.Streak, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task GetStandings_OrdersByTicksDescendingThenEarliestArrivalFirst()
    {
        // steam_1: two solo ticks. steam_2: one solo tick.
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_2"]);

        var standings = await _ledger.GetStandingsAsync(InstanceId);

        Assert.That(standings.Select(s => s.SteamId), Is.EqualTo(new[] { "steam_1", "steam_2" }));
    }

    [Test]
    public async Task GetStandings_EmptyLedger_ReturnsEmpty()
    {
        Assert.That(await _ledger.GetStandingsAsync(InstanceId), Is.Empty);
    }

    [Test]
    public async Task Contestants_TracksEveryoneWhoEverEnteredTheZone_IncludingContestedOnly()
    {
        // steam_1 and steam_2 only ever show up together (contested) — neither is ever credited a
        // tick, but both fought for the hill and must count as contestants.
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1", "steam_2"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_3"]);

        Assert.That(await _ledger.GetContestantCountAsync(InstanceId), Is.EqualTo(3));
    }

    [Test]
    public async Task Contestants_EmptyTicksDoNotCount()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, []);

        Assert.That(await _ledger.GetContestantCountAsync(InstanceId), Is.EqualTo(0));
    }

    [Test]
    public async Task ClearAsync_RemovesControlHolderAndContestantState()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1", "steam_2"]);

        await _ledger.ClearAsync(InstanceId);

        Assert.That(await _ledger.GetStandingsAsync(InstanceId), Is.Empty);
        Assert.That(await _ledger.GetHolderStreakAsync(InstanceId), Is.Null);
        Assert.That(await _ledger.GetContestantCountAsync(InstanceId), Is.EqualTo(0));
    }

    [Test]
    public async Task DifferentInstanceIds_DoNotShareState()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.ApplyPresenceAsync("some_other_instance", ["steam_2"]);

        var standings = await _ledger.GetStandingsAsync(InstanceId);
        Assert.That(standings, Has.Count.EqualTo(1));
        Assert.That(standings[0].SteamId, Is.EqualTo("steam_1"));
    }

    [Test]
    public async Task ApplyPresence_ThreeWayContest_CreditsNobody()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1", "steam_2", "steam_3"]);

        Assert.That(await _ledger.GetStandingsAsync(InstanceId), Is.Empty);
        Assert.That(await _ledger.GetHolderStreakAsync(InstanceId), Is.Null);
    }

    [Test]
    public async Task GetHolderStreak_HolderKeySetButSinceKeyMissing_ReturnsNull()
    {
        // Simulates a partial write (e.g. the process died between the two StringSetAsync calls) —
        // the read side must not crash or invent a streak out of a missing timestamp.
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);
        _store.Strings.TryRemove($"isle:koth:holdersince:{InstanceId}", out _);

        Assert.That(await _ledger.GetHolderStreakAsync(InstanceId), Is.Null);
    }

    [Test]
    public async Task GetStandings_CorruptedControlValue_ExcludesThatEntryButKeepsOthers()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_good"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_bad"]);
        _store.Hashes[$"isle:koth:control:{InstanceId}"]["steam_bad"] = "not-a-number";

        var standings = await _ledger.GetStandingsAsync(InstanceId);

        Assert.That(standings.Select(s => s.SteamId), Is.EqualTo(new[] { "steam_good" }));
    }

    [Test]
    public async Task Contestants_SamePlayerAcrossManyTicks_IsCountedOnce()
    {
        for (var i = 0; i < 5; i++)
            await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1"]);

        Assert.That(await _ledger.GetContestantCountAsync(InstanceId), Is.EqualTo(1));
    }

    [Test]
    public async Task ClearAsync_NeverTouchedInstance_DoesNotThrow() =>
        Assert.DoesNotThrowAsync(() => _ledger.ClearAsync("never_touched_instance"));

    [Test]
    public async Task Contestants_AcrossDifferentInstances_StayIsolated()
    {
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_1", "steam_2"]);
        await _ledger.ApplyPresenceAsync("some_other_instance", ["steam_3"]);

        Assert.That(await _ledger.GetContestantCountAsync(InstanceId), Is.EqualTo(2));
        Assert.That(await _ledger.GetContestantCountAsync("some_other_instance"), Is.EqualTo(1));
    }
}
