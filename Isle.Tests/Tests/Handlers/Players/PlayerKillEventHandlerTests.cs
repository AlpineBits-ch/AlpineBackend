using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class PlayerKillEventHandlerTests
{
    private TestIsleContext _context = null!;

    [SetUp]
    public void SetUp() => _context = TestIsleContext.Create();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_AddsAKillLogWithTheReportedIdsAndWeight()
    {
        var killer = TestData.Player("steam-killer");
        var victim = TestData.Player("steam-victim");
        _context.Players.AddRange(killer, victim);
        await _context.SaveChangesAsync();

        await PlayerKillEventHandler.Handle(
            new PlayerKillEvent { KilerId = killer.Id, VictimId = victim.Id, VictimWeightInKg = 42.5 },
            NullLogger<PlayerKillEventHandler>.Instance,
            _context);
        await _context.SaveChangesAsync();

        var log = _context.KillLogs.Single();
        Assert.That(log.KillerId, Is.EqualTo(killer.Id));
        Assert.That(log.VictimId, Is.EqualTo(victim.Id));
        Assert.That(log.VictimWeightKg, Is.EqualTo(42.5));
    }
}
