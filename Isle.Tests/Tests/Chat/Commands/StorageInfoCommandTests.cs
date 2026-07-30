using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Tests.Helpers;
using IsleBridge.Sdk.Models;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class StorageInfoCommandTests
{
    private TestIsleContext _context = null!;
    private StorageInfoCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _command = new StorageInfoCommand(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_UnknownPlayer_ReturnsAccountErrorMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = "player_missing", Arguments = [] });

        Assert.That(result, Does.Contain("404"));
    }

    [Test]
    public async Task ExecuteAsync_EmptyStorage_ReportsXpAndEmptyStorage()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain($"XP: {player.Xp}"));
        Assert.That(result, Does.Contain("Slots: 0/5"));
        Assert.That(result, Does.Contain("Storage empty."));
    }

    [Test]
    public async Task ExecuteAsync_NullStorage_ReportsZeroSlots()
    {
        var player = TestData.Player("steam-1");
        player.Storage = null!;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Slots: 0/0"));
        Assert.That(result, Does.Contain("Storage empty."));
    }

    [Test]
    public async Task ExecuteAsync_StorageWithSlots_ListsEachSlotInOrderWithGrowthAndDeployedFlag()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var slot1 = player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = "Tyrannosaurus", Growth = 0.6,
            HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
            Mutations = new MutationsData(),
        });
        await Task.Delay(5); // ensure distinct CreatedAt ordering
        var slot2 = player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = "Triceratops", Growth = 0.9,
            HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
            Mutations = new MutationsData(),
        });
        player.Storage.MarkDeployed(slot2.Id);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Slots: 2/5"));
        Assert.That(result, Does.Contain("1. Tyrannosaurus (60"));
        Assert.That(result, Does.Contain("2. Triceratops (90"));
        Assert.That(result, Does.Contain("[out]"));
        Assert.That(slot1.IsDeployed, Is.False);
    }

    [Test]
    public void Name_IsStorage()
    {
        Assert.That(_command.Name, Is.EqualTo("storage"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
