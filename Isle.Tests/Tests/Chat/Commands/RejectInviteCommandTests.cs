using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class RejectInviteCommandTests
{
    private TestIsleContext _context = null!;
    private RejectInviteCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _command = new RejectInviteCommand(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_NoPendingInvites_ReturnsNoneMessage()
    {
        var receiver = TestData.Player("steam-receiver");
        _context.Players.Add(receiver);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = [] });

        Assert.That(result, Does.Contain("no pending invites"));
    }

    [Test]
    public async Task ExecuteAsync_MultiplePendingNoIdentifier_ListsSendersAndDoesNotReject()
    {
        var receiver = TestData.Player("steam-receiver");
        var s1 = TestData.Player("steam-s1", inGameName: "Alice");
        var s2 = TestData.Player("steam-s2", inGameName: "Bob");
        _context.Players.AddRange(receiver, s1, s2);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(s1.Id, receiver.Id));
        _context.PlayerInvites.Add(PlayerInvite.Create(s2.Id, receiver.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = [] });

        Assert.That(result, Does.Contain("2 invites"));
        Assert.That(result, Does.Contain("Alice"));
        Assert.That(result, Does.Contain("Bob"));

        var pendingCount = await _context.PlayerInvites.CountAsync(i => i.Status == PlayerInviteStatus.Pending);
        Assert.That(pendingCount, Is.EqualTo(2));
    }

    [Test]
    public async Task ExecuteAsync_SinglePendingNoIdentifier_RejectsIt()
    {
        var receiver = TestData.Player("steam-receiver");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(receiver, sender);
        await _context.SaveChangesAsync();
        var invite = PlayerInvite.Create(sender.Id, receiver.Id);
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Rejected the invite from Alice"));
        var persisted = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(persisted!.Status, Is.EqualTo(PlayerInviteStatus.Rejected));
    }

    [Test]
    public async Task ExecuteAsync_AmbiguousIdentifier_ReturnsAmbiguousMessage()
    {
        var receiver = TestData.Player("steam-receiver");
        var s1 = TestData.Player("steam-s1", inGameName: "Dupe");
        var s2 = TestData.Player("steam-s2", inGameName: "Dupe");
        _context.Players.AddRange(receiver, s1, s2);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(s1.Id, receiver.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = ["Dupe"] });

        Assert.That(result, Does.Contain("Multiple players"));
    }

    [Test]
    public async Task ExecuteAsync_IdentifierWithNoMatchingInvite_ReturnsNoInviteFromMessage()
    {
        var receiver = TestData.Player("steam-receiver");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        var other = TestData.Player("steam-other", inGameName: "NotSender");
        _context.Players.AddRange(receiver, sender, other);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, receiver.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = ["NotSender"] });

        Assert.That(result, Does.Contain("no pending invite from"));
    }

    [Test]
    public async Task ExecuteAsync_IdentifierMatchingOneOfSeveral_RejectsOnlyThatInvite()
    {
        var receiver = TestData.Player("steam-receiver");
        var s1 = TestData.Player("steam-s1", inGameName: "Alice");
        var s2 = TestData.Player("steam-s2", inGameName: "Bob");
        _context.Players.AddRange(receiver, s1, s2);
        await _context.SaveChangesAsync();
        var invite1 = PlayerInvite.Create(s1.Id, receiver.Id);
        var invite2 = PlayerInvite.Create(s2.Id, receiver.Id);
        _context.PlayerInvites.AddRange(invite1, invite2);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = receiver.Id, Arguments = ["Bob"] });

        Assert.That(result, Does.Contain("Rejected the invite from Bob"));
        Assert.That((await _context.PlayerInvites.FindAsync(invite2.Id))!.Status, Is.EqualTo(PlayerInviteStatus.Rejected));
        Assert.That((await _context.PlayerInvites.FindAsync(invite1.Id))!.Status, Is.EqualTo(PlayerInviteStatus.Pending));
    }

    [Test]
    public void Name_IsReject()
    {
        Assert.That(_command.Name, Is.EqualTo("reject"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
