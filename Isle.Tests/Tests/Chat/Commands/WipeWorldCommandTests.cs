using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services;
using Isle.Api.Services.Rcon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TheIsleEvrimaRconClient;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class WipeWorldCommandTests
{
    private IRconGateway _rcon = null!;
    private WipeWorldCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _rcon = Substitute.For<IRconGateway>();
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var cleaner = new WorldCleaner(_rcon, NullLogger<WorldCleaner>.Instance);
        _command = new WipeWorldCommand(cleaner);
    }

    [Test]
    public async Task ExecuteAsync_TriggersCorpseWipeAndAiToggleOffThenOn()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("World cleanup triggered"));
        await _rcon.Received(3).ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void IsAdminOnly_IsTrue()
    {
        Assert.That(_command.IsAdminOnly, Is.True);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsFalse()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] }), Is.False);
    }

    [Test]
    public void CanRun_AdminContext_ReturnsTrue()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = true, Arguments = [] }), Is.True);
    }
}
