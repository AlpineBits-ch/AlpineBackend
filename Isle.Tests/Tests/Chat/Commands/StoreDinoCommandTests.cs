using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Tests.Helpers;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class StoreDinoCommandTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private StoreDinoCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bridge = Substitute.For<IBridgeClient>();
        _bridge.GetMutationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new MutationsData());
        _command = new StoreDinoCommand(_context, _bridge, NullLogger<StoreDinoCommand>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static CommandContext ValidContext(string playerId) => new()
    {
        PlayerId = playerId,
        PlayerSteam = "steam-1",
        Arguments = [],
        PlayerSpecies = "Tyrannosaurus",
        PlayerGrowth = 0.75,
        HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
    };

    [Test]
    public async Task ExecuteAsync_NoActiveDino_ReturnsUsageMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_1", Arguments = [], PlayerSpecies = null, PlayerGrowth = 0,
        });

        Assert.That(result, Does.Contain("need an active dino"));
    }

    [Test]
    public async Task ExecuteAsync_GrowthBelowThreshold_ReturnsTooYoungMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_1", Arguments = [], PlayerSpecies = "Tyrannosaurus", PlayerGrowth = 0.1,
        });

        Assert.That(result, Does.Contain("too young to store"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownPlayer_ReturnsAccountErrorMessage()
    {
        var result = await _command.ExecuteAsync(ValidContext("player_missing"));

        Assert.That(result, Does.Contain("404"));
    }

    [Test]
    public async Task ExecuteAsync_StorageFull_ReturnsFullMessage()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        for (var i = 0; i < player.Storage.MaxSlotCount; i++)
        {
            player.Storage.StoreDino(new CreateStorageSlotParams
            {
                Species = "Triceratops", Growth = 0.9,
                HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
                Mutations = new MutationsData(),
            });
        }
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(ValidContext(player.Id));

        Assert.That(result, Does.Contain("storage is full"));
    }

    [Test]
    public async Task ExecuteAsync_Success_StoresDinoAndRemovesLivePawn()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(ValidContext(player.Id));

        Assert.That(result, Does.Contain("Stored your Tyrannosaurus"));
        Assert.That(result, Does.Contain("1/5"));

        var storage = await _context.Storages.FindAsync(player.Storage.Id);
        Assert.That(storage!.Slots, Has.Count.EqualTo(1));

        await _bridge.Received(1).KillAsync("steam-1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_PlayerHasNoStorageRow_CreatesOneAndStores()
    {
        var player = TestData.Player("steam-1");
        player.Storage = null!;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(ValidContext(player.Id));

        Assert.That(result, Does.Contain("Stored your"));
    }

    [Test]
    public async Task ExecuteAsync_GetMutationsThrows_StillStoresWithoutMutations()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        _bridge.GetMutationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<MutationsData>(_ => throw new BridgeCommandException(BridgeTestFactory.Fail()));

        var result = await _command.ExecuteAsync(ValidContext(player.Id));

        Assert.That(result, Does.Contain("Stored your"));
    }

    [Test]
    public async Task ExecuteAsync_KillAsyncThrows_StillReportsSuccess()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        _bridge.KillAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new BridgeCommandException(BridgeTestFactory.Fail()));

        var result = await _command.ExecuteAsync(ValidContext(player.Id));

        Assert.That(result, Does.Contain("Stored your"));
    }

    [Test]
    public void Name_IsStore()
    {
        Assert.That(_command.Name, Is.EqualTo("store"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
