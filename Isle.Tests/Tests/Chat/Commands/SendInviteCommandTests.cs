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
public class SendInviteCommandTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private PlayerPresenceManager _presence = null!;
    private PlayerSpawnTracker _spawnTracker = null!;
    private SendInviteCommand _command = null!;

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

        _command = new SendInviteCommand(_context, _bridge, _presence, _spawnTracker);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<CommandContext> FreshSpawnContextAsync(Player sender, ICollection<string> args)
    {
        await _spawnTracker.MarkSpawnedAsync(sender.SteamId);
        return new CommandContext
        {
            PlayerId = sender.Id,
            PlayerSteam = sender.SteamId,
            PlayerSpecies = "Deinosuchus",
            PlayerGrowth = 0.1,
            Arguments = args
        };
    }

    [Test]
    public async Task ExecuteAsync_NoIdentifier_ReturnsUsage()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, []));

        Assert.That(result, Does.Contain("Usage"));
    }

    [Test]
    public async Task ExecuteAsync_NoLivePawn_ReturnsSpawnedInMessage()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();

        var context = new CommandContext
        {
            PlayerId = sender.Id,
            PlayerSteam = sender.SteamId,
            PlayerSpecies = null,
            PlayerGrowth = 0,
            Arguments = ["target"]
        };

        var result = await _command.ExecuteAsync(context);

        Assert.That(result, Does.Contain("spawned in"));
    }

    [Test]
    public async Task ExecuteAsync_NotFreshSpawn_ReturnsEligibilityError()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();

        // Never marked spawned -> GetLastSpawnAsync returns null -> ineligible.
        var context = new CommandContext
        {
            PlayerId = sender.Id,
            PlayerSteam = sender.SteamId,
            PlayerSpecies = "Deinosuchus",
            PlayerGrowth = 0.1,
            Arguments = ["target"]
        };

        var result = await _command.ExecuteAsync(context);

        Assert.That(result, Does.Contain("minutes of spawning"));
    }

    [Test]
    public async Task ExecuteAsync_GrowthTooHigh_ReturnsEligibilityError()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();
        await _spawnTracker.MarkSpawnedAsync(sender.SteamId);

        var context = new CommandContext
        {
            PlayerId = sender.Id,
            PlayerSteam = sender.SteamId,
            PlayerSpecies = "Deinosuchus",
            PlayerGrowth = 0.9,
            Arguments = ["target"]
        };

        var result = await _command.ExecuteAsync(context);

        Assert.That(result, Does.Contain("fresh spawns only"));
    }

    [Test]
    public async Task ExecuteAsync_AmbiguousName_ReturnsAmbiguousMessage()
    {
        var sender = TestData.Player("steam-sender");
        var t1 = TestData.Player("steam-t1", inGameName: "Dupe");
        var t2 = TestData.Player("steam-t2", inGameName: "Dupe");
        _context.Players.AddRange(sender, t1, t2);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, ["Dupe"]));

        Assert.That(result, Does.Contain("Multiple players"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownTarget_ReturnsNotFoundMessage()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, ["nobody"]));

        Assert.That(result, Does.Contain("No player found"));
    }

    [Test]
    public async Task ExecuteAsync_TargetIsSelf_ReturnsCannotInviteSelfMessage()
    {
        var sender = TestData.Player("steam-sender");
        _context.Players.Add(sender);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, [sender.SteamId]));

        Assert.That(result, Does.Contain("not send an invite to yourself"));
    }

    [Test]
    public async Task ExecuteAsync_TargetOffline_ReturnsOfflineMessage()
    {
        var sender = TestData.Player("steam-sender");
        var target = TestData.Player("steam-target", inGameName: "Target");
        _context.Players.AddRange(sender, target);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, [target.SteamId]));

        Assert.That(result, Does.Contain("not online"));
    }

    [Test]
    public async Task ExecuteAsync_AlreadyPendingInvite_ReturnsAlreadyPendingMessage()
    {
        var sender = TestData.Player("steam-sender");
        var target = TestData.Player("steam-target", inGameName: "Target");
        _context.Players.AddRange(sender, target);
        await _context.SaveChangesAsync();
        _context.PlayerInvites.Add(PlayerInvite.Create(sender.Id, target.Id));
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(target.Id);

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, [target.SteamId]));

        Assert.That(result, Does.Contain("already have a pending invite"));
    }

    [Test]
    public async Task ExecuteAsync_ValidInvite_CreatesInviteAndSendsDm()
    {
        var sender = TestData.Player("steam-sender", inGameName: "Sender");
        var target = TestData.Player("steam-target", inGameName: "Target");
        _context.Players.AddRange(sender, target);
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(target.Id);

        var result = await _command.ExecuteAsync(await FreshSpawnContextAsync(sender, [target.SteamId]));

        Assert.That(result, Does.Contain("Invite sent"));
        var invite = await _context.PlayerInvites.FirstOrDefaultAsync(i => i.SenderPlayerId == sender.Id && i.ReceiverPlayerId == target.Id);
        Assert.That(invite, Is.Not.Null);
        Assert.That(invite!.Status, Is.EqualTo(PlayerInviteStatus.Pending));
        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(t => t.Contains(sender.FriendlyId)),
            target.SteamId,
            "VENTA.GG",
            ChatMode.Spatial,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Name_IsInvite()
    {
        Assert.That(_command.Name, Is.EqualTo("invite"));
    }

    [Test]
    public void Cooldown_Is30Seconds()
    {
        Assert.That(_command.Cooldown, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
