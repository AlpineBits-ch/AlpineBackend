using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class LinkInGameNameTests
{
    private TestIsleContext _context = null!;
    private LinkInGameName _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _command = new LinkInGameName(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_KnownPlayer_LinksInGameNameAndPersists()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = player.Id,
            PlayerName = "RexKing",
            Arguments = []
        });

        Assert.That(result, Does.Contain("linked"));
        Assert.That(result, Does.Contain("RexKing"));
        Assert.That(result, Does.Contain(player.FriendlyId));

        var persisted = await _context.Players.FindAsync(player.Id);
        Assert.That(persisted!.InGameName, Is.EqualTo("RexKing"));
    }

    [Test]
    public async Task ExecuteAsync_MissingPlayerName_ReturnsErrorMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_1",
            PlayerName = null!,
            Arguments = []
        });

        Assert.That(result, Does.Contain("400"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownPlayer_ReturnsNotFoundMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_missing",
            PlayerName = "RexKing",
            Arguments = []
        });

        Assert.That(result, Does.Contain("404"));
    }

    [Test]
    public void Name_IsLink()
    {
        Assert.That(_command.Name, Is.EqualTo("link"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
