using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class UserLeftHandlerTests
{
    private TestIsleContext _context = null!;
    private UserLeftHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _handler = new UserLeftHandler(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_KnownPlayer_ReturnsDisconnectedEventForThatPlayer()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new UserLeftIsleServerEvent { SteamId = "steam-1" });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerId, Is.EqualTo(player.Id));
        Assert.That(result.SteamId, Is.EqualTo("steam-1"));
    }

    [Test]
    public async Task Handle_UnknownPlayer_ReturnsNull()
    {
        var result = await _handler.Handle(new UserLeftIsleServerEvent { SteamId = "steam-missing" });

        Assert.That(result, Is.Null);
    }
}
