using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class BuySlotCommandTests
{
    private TestIsleContext _context = null!;
    private BuySlotCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _command = new BuySlotCommand(_context);
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
    public async Task ExecuteAsync_EnoughXp_PurchasesSlotAndSpendsXp()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var startingXp = player.Xp;
        var startingMaxSlots = player.Storage.MaxSlotCount;
        var storageId = player.Storage.Id;

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Slot purchased"));

        var updated = await _context.Players.FindAsync(player.Id);
        Assert.That(updated!.Xp, Is.EqualTo(startingXp - Storage.SlotPurchaseCost));

        var storage = await _context.Storages.FindAsync(storageId);
        Assert.That(storage!.MaxSlotCount, Is.EqualTo(startingMaxSlots + 1));
    }

    [Test]
    public async Task ExecuteAsync_NotEnoughXp_ReturnsErrorAndDoesNotPurchase()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var storageId = player.Storage.Id;

        // Drain the player's starting XP below the slot cost.
        while (player.TrySpendXp(Storage.SlotPurchaseCost))
        {
        }
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Not enough XP"));

        var storage = await _context.Storages.FindAsync(storageId);
        Assert.That(storage!.MaxSlotCount, Is.EqualTo(5));
    }

    [Test]
    public async Task ExecuteAsync_PlayerHasNoStorageRow_CreatesOneBeforePurchasing()
    {
        var player = TestData.Player("steam-1");
        player.Storage = null!;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = [] });

        Assert.That(result, Does.Contain("Slot purchased"));
    }

    [Test]
    public void Name_IsBuySlot()
    {
        Assert.That(_command.Name, Is.EqualTo("buyslot"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
