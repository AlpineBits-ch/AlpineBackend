using Isle.Api.Handlers.Players;
using Isle.Contracts.Bus;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class PlayerByAttributHandlerTests
{
    private TestIsleContext _context = null!;
    private PlayerByAttributHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _handler = new PlayerByAttributHandler(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task HandleBySteamId_KnownSteamId_ReturnsMappedPlayer()
    {
        var player = TestData.Player("steam-1");
        player.LinkUserId("usr-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var response = await _handler.Handle(new GetPlayerBySteamIdRequest { SteamId = "steam-1" }, CancellationToken.None);

        Assert.That(response.Player, Is.Not.Null);
        Assert.That(response.Player!.Id, Is.EqualTo(player.Id));
        Assert.That(response.Player.SteamId, Is.EqualTo("steam-1"));
        Assert.That(response.Player.UserId, Is.EqualTo("usr-1"));
    }

    [Test]
    public async Task HandleBySteamId_UnknownSteamId_ReturnsResponseWithNullPlayer()
    {
        var response = await _handler.Handle(new GetPlayerBySteamIdRequest { SteamId = "steam-missing" }, CancellationToken.None);

        Assert.That(response.Player, Is.Null);
    }

    [Test]
    public async Task HandleByUserId_KnownUserId_ReturnsMappedPlayer()
    {
        var player = TestData.Player("steam-1");
        player.LinkUserId("usr-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var response = await _handler.Handle(new GetPlayerByUserIdRequest { UserId = "usr-1" }, CancellationToken.None);

        Assert.That(response.Player, Is.Not.Null);
        Assert.That(response.Player!.Id, Is.EqualTo(player.Id));
        Assert.That(response.Player.SteamId, Is.EqualTo("steam-1"));
    }

    [Test]
    public async Task HandleByUserId_UnknownUserId_ReturnsResponseWithNullPlayer()
    {
        var response = await _handler.Handle(new GetPlayerByUserIdRequest { UserId = "usr-missing" }, CancellationToken.None);

        Assert.That(response.Player, Is.Null);
    }
}
