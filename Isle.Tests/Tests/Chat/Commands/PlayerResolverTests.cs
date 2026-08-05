using Isle.Api.Chat.Commands;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class PlayerResolverTests
{
    private TestIsleContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ResolveAsync_BySteamId_ReturnsFound()
    {
        var player = TestData.Player("steam-1", inGameName: "RexKing");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerResolver.ResolveAsync(_context, "steam-1");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.Found));
        Assert.That(result.Player!.Id, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task ResolveAsync_ByFriendlyId_ReturnsFound()
    {
        var player = TestData.Player("steam-1", inGameName: "RexKing");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerResolver.ResolveAsync(_context, player.FriendlyId);

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.Found));
        Assert.That(result.Player!.Id, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task ResolveAsync_ByInGameNameCaseInsensitive_ReturnsFound()
    {
        var player = TestData.Player("steam-1", inGameName: "RexKing");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerResolver.ResolveAsync(_context, "rexking");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.Found));
        Assert.That(result.Player!.Id, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task ResolveAsync_DuplicateInGameName_ReturnsAmbiguous()
    {
        var p1 = TestData.Player("steam-1", inGameName: "Dupe");
        var p2 = TestData.Player("steam-2", inGameName: "Dupe");
        _context.Players.AddRange(p1, p2);
        await _context.SaveChangesAsync();

        var result = await PlayerResolver.ResolveAsync(_context, "Dupe");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.Ambiguous));
        Assert.That(result.Player, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_UnknownIdentifier_ReturnsNotFound()
    {
        var result = await PlayerResolver.ResolveAsync(_context, "does-not-exist");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.NotFound));
        Assert.That(result.Player, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_EmptyIdentifier_ReturnsNotFound()
    {
        var result = await PlayerResolver.ResolveAsync(_context, "   ");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.NotFound));
        Assert.That(result.Player, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_NonCanonicalFriendlyIdLookingString_FallsBackToNameLookup()
    {
        // A string that happens to decode via sqids but doesn't round-trip must not be mistaken for a
        // friendly id - it should fall through to the in-game-name lookup (and find nothing here).
        var result = await PlayerResolver.ResolveAsync(_context, "zzzzzz");

        Assert.That(result.Outcome, Is.EqualTo(PlayerResolver.ResolveOutcome.NotFound));
    }
}
