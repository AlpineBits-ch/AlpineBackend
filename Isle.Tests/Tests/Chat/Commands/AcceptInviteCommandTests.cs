using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class AcceptInviteCommandTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private PlayerPresenceManager _presence = null!;
    private PlayerSpawnTracker _spawnTracker = null!;
    private AcceptInviteCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bridge = Substitute.For<IBridgeClient>();
        _bridge.DmAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ChatMode>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _spawnTracker = new PlayerSpawnTracker(cache);

        _command = new AcceptInviteCommand(_context, _bridge, _presence, NullLogger<AcceptInviteCommand>.Instance, _spawnTracker);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<CommandContext> FreshSpawnContextAsync(Player acceptor, ICollection<string> args)
    {
        await _spawnTracker.MarkSpawnedAsync(acceptor.SteamId);
        return new CommandContext
        {
            PlayerId = acceptor.Id,
            PlayerName = acceptor.InGameName ?? "Acceptor",
            PlayerSteam = acceptor.SteamId,
            PlayerSpecies = "Deinosuchus",
            PlayerGrowth = 0.1,
            Arguments = args
        };
    }

    [Test]
    public async Task ExecuteAsync_NoPendingInvites_ReturnsNoneMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        _context.Players.Add(acceptor);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("no pending invites"));
    }

    [Test]
    public async Task ExecuteAsync_MultiplePendingNoIdentifier_ListsSenders()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var s1 = TestData.Player("steam-s1", inGameName: "Alice");
        var s2 = TestData.Player("steam-s2", inGameName: "Bob");
        _context.Players.AddRange(acceptor, s1, s2);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(s1.Id, acceptor.Id));
        _context.PlayerInvites.Add(PlayerInvite.Create(s2.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("2 invites"));
        Assert.That(result, Does.Contain("Alice"));
        Assert.That(result, Does.Contain("Bob"));
    }

    [Test]
    public async Task ExecuteAsync_AmbiguousIdentifier_ReturnsAmbiguousMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var s1 = TestData.Player("steam-s1", inGameName: "Dupe");
        var s2 = TestData.Player("steam-s2", inGameName: "Dupe");
        _context.Players.AddRange(acceptor, s1, s2);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(s1.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, ["Dupe"]));

        Assert.That(result, Does.Contain("Multiple players"));
    }

    [Test]
    public async Task ExecuteAsync_IdentifierWithNoMatchingInvite_ReturnsNoInviteFromMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        var other = TestData.Player("steam-other", inGameName: "NotSender");
        _context.Players.AddRange(acceptor, sender, other);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, ["NotSender"]));

        Assert.That(result, Does.Contain("no pending invite from"));
    }

    [Test]
    public async Task ExecuteAsync_NoLivePawn_ReturnsSpawnedInMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        var context = new CommandContext
        {
            PlayerId = acceptor.Id,
            PlayerSteam = acceptor.SteamId,
            PlayerSpecies = null,
            PlayerGrowth = 0,
            Arguments = []
        };

        var result = await _command.ExecuteAsync(context);

        Assert.That(result, Does.Contain("spawned in"));
    }

    [Test]
    public async Task ExecuteAsync_NotFreshSpawn_ReturnsEligibilityError()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        // Never marked spawned -> ineligible.
        var context = new CommandContext
        {
            PlayerId = acceptor.Id,
            PlayerSteam = acceptor.SteamId,
            PlayerSpecies = "Deinosuchus",
            PlayerGrowth = 0.1,
            Arguments = []
        };

        var result = await _command.ExecuteAsync(context);

        Assert.That(result, Does.Contain("minutes of spawning"));
    }

    [Test]
    public async Task ExecuteAsync_InitiatorOffline_ReturnsOfflineMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, acceptor.Id));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("no longer online"));
    }

    [Test]
    public async Task ExecuteAsync_GetPosThrows_ReturnsCouldNotLocateMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, acceptor.Id));
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(sender.Id);

        _bridge.GetPosAsync(sender.SteamId, Arg.Any<CancellationToken>())
            .Returns<PositionData>(_ => throw new BridgeCommandException(BridgeTestFactory.Fail()));

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("Couldn't locate your host"));
    }

    [Test]
    public async Task ExecuteAsync_TeleportFails_ReturnsTeleportFailedMessage()
    {
        var acceptor = TestData.Player("steam-acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        var invite = PlayerInvite.Create(sender.Id, acceptor.Id);
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(sender.Id);

        _bridge.GetPosAsync(sender.SteamId, Arg.Any<CancellationToken>())
            .Returns(new PositionData { Pos = new Position { X = 1, Y = 2, Z = 3 } });
        _bridge.TeleportAsync(acceptor.SteamId, 1, 2, 3, Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Fail());

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("Teleport failed"));
        var persisted = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(persisted!.Status, Is.EqualTo(PlayerInviteStatus.Pending));
    }

    [Test]
    public async Task ExecuteAsync_Success_AcceptsTeleportsAndNotifiesInitiator()
    {
        var acceptor = TestData.Player("steam-acceptor", inGameName: "Acceptor");
        var sender = TestData.Player("steam-sender", inGameName: "Alice");
        _context.Players.AddRange(acceptor, sender);
        await _context.SaveChangesAsync();
        var invite = PlayerInvite.Create(sender.Id, acceptor.Id);
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(sender.Id);

        _bridge.GetPosAsync(sender.SteamId, Arg.Any<CancellationToken>())
            .Returns(new PositionData { Pos = new Position { X = 10, Y = 20, Z = 30 }, Rot = new Rotation { Yaw = 90 } });
        _bridge.TeleportAsync(acceptor.SteamId, 10, 20, 30, 90, Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(acceptor, []));

        Assert.That(result, Does.Contain("Teleported to Alice"));
        var persisted = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(persisted!.Status, Is.EqualTo(PlayerInviteStatus.Accepted));
        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(t => t.Contains("accepted your invite")),
            sender.SteamId,
            "VENTA.GG",
            ChatMode.Spatial,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Name_IsAccept()
    {
        Assert.That(_command.Name, Is.EqualTo("accept"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
