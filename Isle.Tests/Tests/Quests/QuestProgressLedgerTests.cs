using Isle.Api.Services.Quests;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class QuestProgressLedgerTests
{
    private QuestProgressLedger _ledger = null!;
    private const string InstanceId = "qi_test";

    [SetUp]
    public void SetUp() =>
        _ledger = new QuestProgressLedger(RedisTestFactory.Create(), NullLogger<QuestProgressLedger>.Instance);

    [Test]
    public async Task CreditPresence_AccumulatesTicksAcrossCalls()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);

        var qualified = await _ledger.GetQualifiedAsync(InstanceId, minTicks: 2);

        Assert.That(qualified.Single().Ticks, Is.EqualTo(2));
    }

    [Test]
    public async Task CreditPresence_IgnoresBlankSteamIds()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "", "  ", null!]);

        Assert.That(await _ledger.VisitorCountAsync(InstanceId), Is.EqualTo(1));
    }

    [Test]
    public async Task CreditPresence_EmptyList_IsANoOp()
    {
        await _ledger.CreditPresenceAsync(InstanceId, []);

        Assert.That(await _ledger.VisitorCountAsync(InstanceId), Is.EqualTo(0));
    }

    [Test]
    public async Task GetQualified_FiltersBelowMinTicks()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "steam_2"]);

        var qualified = await _ledger.GetQualifiedAsync(InstanceId, minTicks: 2);

        Assert.That(qualified.Select(q => q.SteamId), Is.EqualTo(new[] { "steam_1" }));
    }

    [Test]
    public async Task GetQualified_OrdersByEarliestArrivalFirst_ThenTicksDescending()
    {
        // steam_1 arrives first tick, steam_2 arrives second tick - both end with 2+ ticks.
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "steam_2"]);
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "steam_2"]);

        var qualified = await _ledger.GetQualifiedAsync(InstanceId, minTicks: 1);

        Assert.That(qualified.Select(q => q.SteamId), Is.EqualTo(new[] { "steam_1", "steam_2" }));
    }

    [Test]
    public async Task VisitorCount_CountsEveryoneRegardlessOfTickThreshold()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "steam_2"]);

        Assert.That(await _ledger.VisitorCountAsync(InstanceId), Is.EqualTo(2));
    }

    [Test]
    public async Task ClearAsync_RemovesPresenceAndFirstSeenState()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);

        await _ledger.ClearAsync(InstanceId);

        Assert.That(await _ledger.VisitorCountAsync(InstanceId), Is.EqualTo(0));
        Assert.That(await _ledger.GetQualifiedAsync(InstanceId, minTicks: 0), Is.Empty);
    }

    [Test]
    public async Task DifferentInstances_DoNotShareState()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);
        await _ledger.CreditPresenceAsync("qi_other", ["steam_2"]);

        Assert.That(await _ledger.VisitorCountAsync(InstanceId), Is.EqualTo(1));
    }

    [Test]
    public async Task CreditPresence_DuplicateSteamIdInOneCall_CreditsOnlyOneTick()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1", "steam_1", "steam_1"]);

        var qualified = await _ledger.GetQualifiedAsync(InstanceId, minTicks: 1);
        Assert.That(qualified.Single().Ticks, Is.EqualTo(1));
    }

    [Test]
    public async Task GetQualified_MinTicksZero_ReturnsEveryoneEverCredited()
    {
        await _ledger.CreditPresenceAsync(InstanceId, ["steam_1"]);

        var qualified = await _ledger.GetQualifiedAsync(InstanceId, minTicks: 0);

        Assert.That(qualified.Select(q => q.SteamId), Is.EqualTo(new[] { "steam_1" }));
    }
}
