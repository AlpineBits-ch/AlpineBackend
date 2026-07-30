using Isle.Api.Handlers.Players;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class CreatePlayerIfNotExistsAndEmmitPlayerHandlerTests
{
    private TestIsleContext _context = null!;
    private IMessageBus _bus = null!;
    private CreatePlayerIfNotExistsAndEmmitPlayerHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bus = Substitute.For<IMessageBus>();
        _handler = new CreatePlayerIfNotExistsAndEmmitPlayerHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_ExistingPlayer_ReturnsConnectedEventWithoutInvokingCreateCommand()
    {
        var player = TestData.Player("steam-1");
        player.LinkUserId("usr-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new UserJoinedIsleServerEvent { SteamId = "steam-1" }, _context, _bus);

        Assert.That(result, Is.InstanceOf<PlayerConnectedEvent>());
        var connected = (PlayerConnectedEvent)result!;
        Assert.That(connected.PlayerId, Is.EqualTo(player.Id));
        Assert.That(connected.SteamId, Is.EqualTo("steam-1"));
        Assert.That(connected.UserId, Is.EqualTo("usr-1"));
        await _bus.DidNotReceive().InvokeAsync(Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task Handle_NoExistingPlayer_InvokesCreateCommandAndReturnsTheNewlyCreatedPlayer()
    {
        _bus.InvokeAsync(Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(ci => CreateThePlayerAsync((CreatePlayerCommand)ci.Arg<object>()));

        var result = await _handler.Handle(new UserJoinedIsleServerEvent { SteamId = "steam-new" }, _context, _bus);

        Assert.That(result, Is.InstanceOf<PlayerConnectedEvent>());
        var connected = (PlayerConnectedEvent)result!;
        Assert.That(connected.SteamId, Is.EqualTo("steam-new"));
        Assert.That(_context.Players.Single().SteamId, Is.EqualTo("steam-new"));
    }

    [Test]
    public void Handle_NoExistingPlayerAndCreateCommandNeverMaterializesOne_Throws()
    {
        _bus.InvokeAsync(Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Task.CompletedTask);

        Assert.ThrowsAsync<Exception>(() => _handler.Handle(new UserJoinedIsleServerEvent { SteamId = "steam-ghost" }, _context, _bus));
    }

    private async Task CreateThePlayerAsync(CreatePlayerCommand command)
    {
        var player = Player.Create(new CreatePlayerArgs { SteamId = command.SteamId, UserId = command.UserId, IsAdmin = command.IsAdmin });
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
    }
}
