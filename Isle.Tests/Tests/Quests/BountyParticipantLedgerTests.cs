using Isle.Api.Services.Quests;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Quests;

[TestFixture]
public class BountyParticipantLedgerTests
{
    private BountyParticipantLedger _ledger = null!;
    private const string InstanceId = "qi_bounty_test";

    [SetUp]
    public void SetUp() =>
        _ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);

    [Test]
    public async Task Record_AccumulatesDamageAcrossCalls()
    {
        await _ledger.RecordAsync(InstanceId, "steam_1", 100, DateTimeOffset.UtcNow);
        await _ledger.RecordAsync(InstanceId, "steam_1", 50, DateTimeOffset.UtcNow);

        var participants = await _ledger.GetParticipantsAsync(InstanceId);

        Assert.That(participants.Single().Damage, Is.EqualTo(150));
    }

    [Test]
    public async Task Record_ZeroOrNegativeDamage_IsIgnored()
    {
        await _ledger.RecordAsync(InstanceId, "steam_1", 0, DateTimeOffset.UtcNow);
        await _ledger.RecordAsync(InstanceId, "steam_1", -10, DateTimeOffset.UtcNow);

        Assert.That(await _ledger.GetParticipantsAsync(InstanceId), Is.Empty);
    }

    [Test]
    public async Task GetParticipants_FiltersBelowMinDamageForParticipation()
    {
        await _ledger.RecordAsync(InstanceId, "steam_bystander", BountyParticipantLedger.MinDamageForParticipation - 1, DateTimeOffset.UtcNow);
        await _ledger.RecordAsync(InstanceId, "steam_fighter", BountyParticipantLedger.MinDamageForParticipation, DateTimeOffset.UtcNow);

        var participants = await _ledger.GetParticipantsAsync(InstanceId);

        Assert.That(participants.Select(p => p.SteamId), Is.EqualTo(new[] { "steam_fighter" }));
    }

    [Test]
    public async Task GetParticipants_OrdersByDamageDescending()
    {
        await _ledger.RecordAsync(InstanceId, "steam_low", 150, DateTimeOffset.UtcNow);
        await _ledger.RecordAsync(InstanceId, "steam_high", 500, DateTimeOffset.UtcNow);

        var participants = await _ledger.GetParticipantsAsync(InstanceId);

        Assert.That(participants.Select(p => p.SteamId), Is.EqualTo(new[] { "steam_high", "steam_low" }));
    }

    [Test]
    public async Task LastAttacker_ReturnsWhoeverHitMostRecently_RegardlessOfTotalDamage()
    {
        var earlier = DateTimeOffset.UtcNow.AddSeconds(-30);
        var later = DateTimeOffset.UtcNow;

        // steam_big hit hard but long ago; steam_recent landed a small hit just now.
        await _ledger.RecordAsync(InstanceId, "steam_big", 500, earlier);
        await _ledger.RecordAsync(InstanceId, "steam_recent", 10, later);

        var lastAttacker = await _ledger.LastAttackerAsync(InstanceId);

        Assert.That(lastAttacker!.SteamId, Is.EqualTo("steam_recent"));
    }

    [Test]
    public async Task LastAttacker_NoDamageRecorded_ReturnsNull()
    {
        Assert.That(await _ledger.LastAttackerAsync(InstanceId), Is.Null);
    }

    [Test]
    public async Task ClearAsync_RemovesDamageAndLastHitState()
    {
        await _ledger.RecordAsync(InstanceId, "steam_1", 200, DateTimeOffset.UtcNow);

        await _ledger.ClearAsync(InstanceId);

        Assert.That(await _ledger.GetParticipantsAsync(InstanceId), Is.Empty);
        Assert.That(await _ledger.LastAttackerAsync(InstanceId), Is.Null);
    }

    [Test]
    public async Task Record_CumulativeHitsCrossingTheParticipationThreshold_QualifiesOnceTotalled()
    {
        var half = BountyParticipantLedger.MinDamageForParticipation / 2;
        await _ledger.RecordAsync(InstanceId, "steam_1", half, DateTimeOffset.UtcNow);

        // A single hit left them below the bar; nothing here should count them yet.
        Assert.That(await _ledger.GetParticipantsAsync(InstanceId), Is.Empty);

        await _ledger.RecordAsync(InstanceId, "steam_1", half, DateTimeOffset.UtcNow);

        Assert.That(await _ledger.GetParticipantsAsync(InstanceId), Has.Count.EqualTo(1));
    }
}
