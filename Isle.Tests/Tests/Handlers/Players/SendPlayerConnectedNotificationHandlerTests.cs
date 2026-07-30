using Isle.Api.Handlers.Players;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class SendPlayerConnectedNotificationHandlerTests
{
    private FakeIsleHubContext _hub = null!;
    private SendPlayerConnectedNotificationHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _hub = new FakeIsleHubContext();
        _handler = new SendPlayerConnectedNotificationHandler(_hub, NullLogger<SendPlayerConnectedNotificationHandler>.Instance);
    }

    [Test]
    public async Task Handle_LinkedPlayer_SendsSocketMessageToTheLinkedUser()
    {
        await _handler.Handle(new PlayerConnectedEvent { PlayerId = "player-1", SteamId = "steam-1", UserId = "usr-1" });

        Assert.That(_hub.ClientsTyped.SentMessages, Has.Count.EqualTo(1));
        var (userId, method, args) = _hub.ClientsTyped.SentMessages[0];
        Assert.That(userId, Is.EqualTo("usr-1"));
        Assert.That(method, Is.EqualTo("isle.PlayerJoined"));
        Assert.That(args[0]!.GetType().GetProperty("playerId")!.GetValue(args[0]), Is.EqualTo("player-1"));
    }

    [Test]
    public async Task Handle_NoLinkedUser_DoesNotSendAnything()
    {
        await _handler.Handle(new PlayerConnectedEvent { PlayerId = "player-1", SteamId = "steam-1", UserId = null });

        Assert.That(_hub.ClientsTyped.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Handle_WhitespaceUserId_DoesNotSendAnything()
    {
        await _handler.Handle(new PlayerConnectedEvent { PlayerId = "player-1", SteamId = "steam-1", UserId = "   " });

        Assert.That(_hub.ClientsTyped.SentMessages, Is.Empty);
    }
}
