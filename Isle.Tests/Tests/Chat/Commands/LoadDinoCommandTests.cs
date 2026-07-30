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
public class LoadDinoCommandTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private LoadDinoCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bridge = Substitute.For<IBridgeClient>();
        _command = new LoadDinoCommand(_context, _bridge, NullLogger<LoadDinoCommand>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<(Player Player, StorageSlot Slot)> CreatePlayerWithSlotAsync(double growth = 0.9, string species = "Tyrannosaurus")
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var slot = player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = species, Growth = growth,
            HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
            Mutations = new MutationsData(),
        });
        await _context.SaveChangesAsync();
        return (player, slot);
    }

    [Test]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase(null)]
    public async Task ExecuteAsync_InvalidSlotArgument_ReturnsUsageMessage(string? slotArg)
    {
        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_1", Arguments = slotArg is null ? [] : [slotArg],
        });

        Assert.That(result, Does.Contain("Usage: !load"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownPlayer_ReturnsEmptyStorageMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = "player_missing", Arguments = ["1"] });

        Assert.That(result, Is.EqualTo("Your storage is empty."));
    }

    [Test]
    public async Task ExecuteAsync_NoSlotsStored_ReturnsEmptyStorageMessage()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"] });

        Assert.That(result, Is.EqualTo("Your storage is empty."));
    }

    [Test]
    public async Task ExecuteAsync_SlotNumberOutOfRange_ReturnsRangeMessage()
    {
        var (player, _) = await CreatePlayerWithSlotAsync();

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["5"] });

        Assert.That(result, Does.Contain("You only have 1 slot(s) in use"));
    }

    [Test]
    public async Task ExecuteAsync_SlotBelowGrowthThreshold_ReturnsTooYoungMessage()
    {
        // Storage.StoreDino itself enforces the growth threshold at store time, so a
        // below-threshold slot can only exist if the threshold was raised after the slot was
        // stored.
        var (player, slot) = await CreatePlayerWithSlotAsync(growth: 0.9);
        slot.Growth = 0.2;

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"] });

        Assert.That(result, Does.Contain("below the"));
        Assert.That(result, Does.Contain("growth threshold"));
    }

    [Test]
    public async Task ExecuteAsync_SwapFailsNoPawn_ReturnsNeedLivePawnMessage()
    {
        var (player, _) = await CreatePlayerWithSlotAsync();
        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Fail("NO_PAWN"));

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("need a live dino to load onto"));
    }

    [Test]
    public async Task ExecuteAsync_SwapFailsNotWhitelisted_ReturnsNotAllowedMessage()
    {
        var (player, _) = await CreatePlayerWithSlotAsync();
        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Fail("NOT_WHITELISTED"));

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("Tyrannosaurus is not allowed on this server right now."));
    }

    [Test]
    public async Task ExecuteAsync_SwapFailsOtherCode_ReturnsGenericRetryMessage()
    {
        var (player, _) = await CreatePlayerWithSlotAsync();
        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Fail("RATE_LIMITED"));

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("Could not load your dino"));
    }

    [Test]
    public async Task ExecuteAsync_SwapSucceedsWithHealthData_CallsSetStatsWithRestoredVitalsAndMarksDeployed()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = "Tyrannosaurus",
            Growth = 0.9,
            HealthData = new DinoHealthData { Health = 80, Stamina = 70, Hunger = 60, Thirst = 50 },
            Mutations = new MutationsData(),
        });
        await _context.SaveChangesAsync();

        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _bridge.SetStatsAsync(Arg.Any<string>(), Arg.Any<SetStatsArgs>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _bridge.SetMutationsAsync(Arg.Any<string>(), Arg.Any<SetMutationsArgs>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("Loaded your"));
        await _bridge.Received(1).SetStatsAsync("steam-1", Arg.Is<SetStatsArgs>(
            s => s.Health == 80 && s.Stamina == 70 && s.Hunger == 60 && s.Thirst == 50), Arg.Any<CancellationToken>());
        await _bridge.Received(1).SetMutationsAsync(Arg.Any<string>(), Arg.Any<SetMutationsArgs>(), Arg.Any<CancellationToken>());

        var storage = await _context.Storages.FindAsync(player.Storage.Id);
        Assert.That(storage!.Slots.Single().IsDeployed, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_SetStatsThrows_StillReturnsSuccess()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = "Tyrannosaurus", Growth = 0.9,
            HealthData = new DinoHealthData { Health = 1, Stamina = 1, Hunger = 1, Thirst = 1 },
            Mutations = new MutationsData(),
        });
        await _context.SaveChangesAsync();

        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _bridge.SetStatsAsync(Arg.Any<string>(), Arg.Any<SetStatsArgs>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new InvalidOperationException("boom"));

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("Loaded your"));
    }

    [Test]
    public async Task ExecuteAsync_SetMutationsThrows_StillReturnsSuccess()
    {
        var (player, _) = await CreatePlayerWithSlotAsync();

        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _bridge.SetMutationsAsync(Arg.Any<string>(), Arg.Any<SetMutationsArgs>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new InvalidOperationException("boom"));

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        Assert.That(result, Does.Contain("Loaded your"));
    }

    [Test]
    public async Task ExecuteAsync_MutationsRestoredCaseInsensitively()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = "Tyrannosaurus",
            Growth = 0.9,
            HealthData = new DinoHealthData { Health = 100, Hunger = 100, Stamina = 100, Thirst = 100 },
            Mutations = new MutationsData
            {
                Slots = new Dictionary<string, string> { ["slot1"] = "MutA", ["Parent2"] = "MutB" },
                ElderStacks = 3,
                Unlocks = ["Unlock1"],
            },
        });
        await _context.SaveChangesAsync();

        _bridge.SwapAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());
        _bridge.SetMutationsAsync(Arg.Any<string>(), Arg.Any<SetMutationsArgs>(), Arg.Any<CancellationToken>())
            .Returns(BridgeTestFactory.Ok());

        await _command.ExecuteAsync(new CommandContext { PlayerId = player.Id, Arguments = ["1"], PlayerSteam = "steam-1" });

        await _bridge.Received(1).SetMutationsAsync("steam-1", Arg.Is<SetMutationsArgs>(
            m => m.Slot1 == "MutA" && m.Parent2 == "MutB" && m.ElderStacks == 3 && m.Unlocks!.Single() == "Unlock1"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Name_IsLoad()
    {
        Assert.That(_command.Name, Is.EqualTo("load"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }
}
