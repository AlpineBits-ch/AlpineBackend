using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class WhoAmICommandTests
{
    private TestIsleContext _context = null!;
    private WhoAmICommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _command = new WhoAmICommand(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_LinkedPlayer_ShowsInGameName()
    {
        var player = TestData.Player("steam-1", inGameName: "RexKing");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("RexKing"));
        Assert.That(result, Does.Contain("steam-1"));
    }

    [Test]
    public async Task ExecuteAsync_UnlinkedPlayer_PromptsToUseLinkCommand()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("!link"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownPlayer_ReturnsAccountErrorMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = "player_missing", Arguments = [] });

        Assert.That(result, Does.Contain("404"));
    }

    [Test]
    public void Name_IsId()
    {
        Assert.That(_command.Name, Is.EqualTo("id"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
