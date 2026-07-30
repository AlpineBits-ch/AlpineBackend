using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Infrastructure.Persistence;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class HelpCommandTests
{
    private TestIsleContext _context = null!;
    private AutoMockServiceProvider _provider = null!;
    private HelpCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _provider = new AutoMockServiceProvider().With(typeof(MicroserviceContext), _context);
        _command = new HelpCommand(_provider);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_NoArguments_NonAdmin_ListsNonAdminCommandsOnly()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = [] });

        Assert.That(result, Does.StartWith("Commands:"));
        Assert.That(result, Does.Contain("debug"));
        Assert.That(result, Does.Contain("link"));
        Assert.That(result, Does.Contain("invite"));
        Assert.That(result, Does.Contain("accept"));
        Assert.That(result, Does.Contain("reject"));
        Assert.That(result, Does.Contain("help"));
        Assert.That(result, Does.Not.Contain("wipeworld"));
    }

    [Test]
    public async Task ExecuteAsync_NoArguments_Admin_IncludesAdminOnlyCommands()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = true, Arguments = [] });

        Assert.That(result, Does.Contain("wipeworld"));
    }

    [Test]
    public async Task ExecuteAsync_RequestKnownCommand_ReturnsNameAndDescription()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["debug"] });

        Assert.That(result, Does.StartWith("!debug:"));
        Assert.That(result, Does.Contain("Debug command, to see if the game actually deals with commands"));
    }

    [Test]
    public async Task ExecuteAsync_RequestWithBangPrefix_StripsPrefixAndResolves()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["!debug"] });

        Assert.That(result, Does.StartWith("!debug:"));
    }

    [Test]
    public async Task ExecuteAsync_RequestCommandIsCaseInsensitive()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["DEBUG"] });

        Assert.That(result, Does.StartWith("!debug:"));
    }

    [Test]
    public async Task ExecuteAsync_RequestCommandWithCooldown_AppendsCooldownSuffix()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["invite"] });

        Assert.That(result, Does.Contain("(cooldown 30s)"));
    }

    [Test]
    public async Task ExecuteAsync_RequestCommandWithoutCooldown_OmitsCooldownSuffix()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["debug"] });

        Assert.That(result, Does.Not.Contain("cooldown"));
    }

    [Test]
    public async Task ExecuteAsync_RequestAdminOnlyCommandAsAdmin_AppendsAdminOnlySuffix()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = true, Arguments = ["wipeworld"] });

        Assert.That(result, Does.StartWith("!wipeworld:"));
        Assert.That(result, Does.Contain("(admin only)"));
    }

    [Test]
    public async Task ExecuteAsync_RequestAdminOnlyCommandAsNonAdmin_IsHiddenAsNotFound()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["wipeworld"] });

        Assert.That(result, Does.Contain("No command named wipeworld"));
    }

    [Test]
    public async Task ExecuteAsync_RequestUnknownCommand_ReturnsNotFoundMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { IsAdmin = false, Arguments = ["bogus"] });

        Assert.That(result, Does.Contain("No command named bogus"));
    }

    [Test]
    public void Name_IsHelp()
    {
        Assert.That(_command.Name, Is.EqualTo("help"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
