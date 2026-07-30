using Isle.Api.Chat;
using Isle.Api.Chat.Commands;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class DebugCommandTests
{
    private DebugCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _command = new DebugCommand();
    }

    [Test]
    public async Task ExecuteAsync_ReturnsMessageContainingPlayerName()
    {
        var result = await _command.ExecuteAsync(new CommandContext { PlayerName = "RexKing", Arguments = [] });

        Assert.That(result, Does.Contain("RexKing"));
        Assert.That(result, Does.Contain("Debug command received"));
    }

    [Test]
    public void Name_IsDebug()
    {
        Assert.That(_command.Name, Is.EqualTo("debug"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsTrue()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] }), Is.True);
    }
}
