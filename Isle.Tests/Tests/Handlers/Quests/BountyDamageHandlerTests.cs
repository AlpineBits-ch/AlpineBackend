using Isle.Api.Handlers.Quests;
using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Quests;

[TestFixture]
public class BountyDamageHandlerTests
{
    private BountyRegistry _registry = null!;
    private BountyParticipantLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        _ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
    }

    private Task HandleAsync(string attacker, string victim, double damage) =>
        BountyDamageHandler.Handle(
            new PlayerDamagedEvent { AttackerSteamId = attacker, VictimSteamId = victim, Damage = damage },
            _registry, _ledger, CancellationToken.None);

    [Test]
    public async Task Handle_VictimNotMarked_RecordsNothing()
    {
        await HandleAsync("steam_attacker", "steam_victim", 100);

        Assert.That(await _ledger.GetParticipantsAsync("qi_1"), Is.Empty);
    }

    [Test]
    public async Task Handle_VictimMarked_CreditsTheAttackerOnTheBountysLedger()
    {
        await _registry.MarkAsync(new BountyMark("qi_1", "player_victim", "steam_victim", "Rex", DateTimeOffset.UtcNow.AddMinutes(20)));

        await HandleAsync("steam_attacker", "steam_victim", 250);

        var participants = await _ledger.GetParticipantsAsync("qi_1");
        Assert.That(participants.Single().SteamId, Is.EqualTo("steam_attacker"));
        Assert.That(participants.Single().Damage, Is.EqualTo(250));
    }

    [Test]
    public async Task Handle_SelfDamageOnAMarkedPlayer_CreditsNobody()
    {
        await _registry.MarkAsync(new BountyMark("qi_1", "player_victim", "steam_victim", "Rex", DateTimeOffset.UtcNow.AddMinutes(20)));

        await HandleAsync("steam_victim", "steam_victim", 250);

        Assert.That(await _ledger.GetParticipantsAsync("qi_1"), Is.Empty);
    }

    [Test]
    public async Task Handle_VictimMarkedExpired_RecordsNothing()
    {
        await _registry.MarkAsync(new BountyMark("qi_1", "player_victim", "steam_victim", "Rex", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await HandleAsync("steam_attacker", "steam_victim", 250);

        Assert.That(await _ledger.GetParticipantsAsync("qi_1"), Is.Empty);
    }
}
