using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class PlayerKillfeedHandlerTests
{
    private TestIsleContext _context = null!;

    [SetUp]
    public void SetUp() => _context = TestIsleContext.Create();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_BothPlayersRegistered_ReturnsResolvedKillEvent()
    {
        var killer = TestData.Player("steam-killer");
        var victim = TestData.Player("steam-victim");
        _context.Players.AddRange(killer, victim);
        await _context.SaveChangesAsync();

        var result = await PlayerKillfeedHandler.Handle(
            new PlayerKillfeedReportedEvent { KillerSteamId = "steam-killer", VictimSteamId = "steam-victim", VictimWeightInKg = 12.3 },
            _context, NullLogger<PlayerKillfeedHandler>.Instance, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.KilerId, Is.EqualTo(killer.Id));
        Assert.That(result.VictimId, Is.EqualTo(victim.Id));
        Assert.That(result.VictimWeightInKg, Is.EqualTo(12.3));
    }

    [Test]
    public async Task Handle_UnregisteredKiller_ReturnsNull()
    {
        var victim = TestData.Player("steam-victim");
        _context.Players.Add(victim);
        await _context.SaveChangesAsync();

        var result = await PlayerKillfeedHandler.Handle(
            new PlayerKillfeedReportedEvent { KillerSteamId = "steam-missing", VictimSteamId = "steam-victim" },
            _context, NullLogger<PlayerKillfeedHandler>.Instance, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Handle_UnregisteredVictim_ReturnsNull()
    {
        var killer = TestData.Player("steam-killer");
        _context.Players.Add(killer);
        await _context.SaveChangesAsync();

        var result = await PlayerKillfeedHandler.Handle(
            new PlayerKillfeedReportedEvent { KillerSteamId = "steam-killer", VictimSteamId = "steam-missing" },
            _context, NullLogger<PlayerKillfeedHandler>.Instance, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Handle_NeitherPlayerRegistered_ReturnsNull()
    {
        var result = await PlayerKillfeedHandler.Handle(
            new PlayerKillfeedReportedEvent { KillerSteamId = "steam-missing-1", VictimSteamId = "steam-missing-2" },
            _context, NullLogger<PlayerKillfeedHandler>.Instance, CancellationToken.None);

        Assert.That(result, Is.Null);
    }
}
