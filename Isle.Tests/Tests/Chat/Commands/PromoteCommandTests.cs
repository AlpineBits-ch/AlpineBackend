using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Contracts.Commands;
using Isle.Tests.Helpers;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class PromoteCommandTests
{
    private TestIsleContext _context = null!;
    private IMessageBus _bus = null!;
    private PromoteCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bus = Substitute.For<IMessageBus>();
        _command = new PromoteCommand(_context, _bus);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_NoArguments_ReturnsUsageMessageAndPublishesNothing()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("Usage"));
        await _bus.DidNotReceive().PublishAsync(Arg.Any<ChangePlayerAdminStatusCommand>());
    }

    [Test]
    public async Task ExecuteAsync_UnknownSteamId_ReturnsNotFoundMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["steam-unknown"] });

        Assert.That(result, Does.Contain("not found"));
        await _bus.DidNotReceive().PublishAsync(Arg.Any<ChangePlayerAdminStatusCommand>());
    }

    [Test]
    public async Task ExecuteAsync_KnownSteamId_PublishesChangeAdminStatusCommand()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["steam-1"] });

        Assert.That(result, Does.Contain("promoted"));
        await _bus.Received(1).PublishAsync(Arg.Is<ChangePlayerAdminStatusCommand>(
            c => c.PlayerId == player.Id && c.IsAdmin));
    }

    [Test]
    public void IsAdminOnly_IsTrue()
    {
        Assert.That(_command.IsAdminOnly, Is.True);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsFalse()
    {
        var canRun = _command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] });

        Assert.That(canRun, Is.False);
    }

    [Test]
    public void CanRun_AdminContext_ReturnsTrue()
    {
        var canRun = _command.CanRun(new CommandContext { IsAdmin = true, Arguments = [] });

        Assert.That(canRun, Is.True);
    }
}
