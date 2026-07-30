using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class SendPlayerDisconnectNotificationHandlerTests
{
    private TestIsleContext _context = null!;
    private FakeIsleHubContext _hub = null!;
    private SendPlayerDisconnectNotificationHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _hub = new FakeIsleHubContext();
        _handler = new SendPlayerDisconnectNotificationHandler(_hub, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_LinkedPlayer_SendsSocketMessageToTheLinkedUser()
    {
        var player = TestData.Player("steam-1");
        player.LinkUserId("usr-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        await _handler.Handle(new PlayerDisconnectedEvent { PlayerId = player.Id, SteamId = "steam-1" });

        Assert.That(_hub.ClientsTyped.SentMessages, Has.Count.EqualTo(1));
        var (userId, method, args) = _hub.ClientsTyped.SentMessages[0];
        Assert.That(userId, Is.EqualTo("usr-1"));
        Assert.That(method, Is.EqualTo("isle.PlayerDisconnected"));
        Assert.That(args[0]!.GetType().GetProperty("steamId")!.GetValue(args[0]), Is.EqualTo("steam-1"));
    }

    [Test]
    public async Task Handle_PlayerWithNoLinkedUser_DoesNotSendAnything()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        await _handler.Handle(new PlayerDisconnectedEvent { PlayerId = player.Id, SteamId = "steam-1" });

        Assert.That(_hub.ClientsTyped.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Handle_UnknownPlayer_DoesNotSendAnythingOrThrow()
    {
        Assert.DoesNotThrowAsync(() => _handler.Handle(new PlayerDisconnectedEvent { PlayerId = "player-missing", SteamId = "steam-missing" }));
        Assert.That(_hub.ClientsTyped.SentMessages, Is.Empty);
    }
}
