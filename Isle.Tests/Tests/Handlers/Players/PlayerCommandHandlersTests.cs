using Identity.Contracts.Bus.Events;
using Isle.Api.Handlers.Players;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Domain.Entity;
using Isle.Tests.Helpers;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Players;

[TestFixture]
public class PlayerCommandHandlersTests
{
    private TestIsleContext _context = null!;

    [SetUp]
    public void SetUp() => _context = TestIsleContext.Create();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── CreatePlayerCommandHandler ───────────────────────────────────────────

    [Test]
    public async Task CreatePlayer_AddsPlayerAndReturnsItsId()
    {
        var handler = new CreatePlayerCommandHandler(_context);

        var response = await handler.Handle(new CreatePlayerCommand { SteamId = "steam-1", UserId = "usr_1", IsAdmin = true });
        await _context.SaveChangesAsync();

        Assert.That(response.Failures, Is.Empty);
        var player = _context.Players.Single();
        Assert.That(player.Id, Is.EqualTo(response.PlayerId));
        Assert.That(player.SteamId, Is.EqualTo("steam-1"));
        Assert.That(player.UserId, Is.EqualTo("usr_1"));
        Assert.That(player.IsAdmin, Is.True);
    }

    // ── ChangePlayerAdminStatusCommandHandler ────────────────────────────────

    [Test]
    public async Task ChangeAdminStatus_KnownPlayer_PromotesToAdmin()
    {
        var player = TestData.Player("steam-1");
        player.IsAdmin = false;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new ChangePlayerAdminStatusCommandHandler(_context, NullLogger<ChangePlayerAdminStatusCommandHandler>.Instance);

        await handler.Handle(new ChangePlayerAdminStatusCommand { PlayerId = player.Id, IsAdmin = true });
        await _context.SaveChangesAsync();

        Assert.That(_context.Players.Single().IsAdmin, Is.True);
    }

    [Test]
    public async Task ChangeAdminStatus_KnownPlayer_DemotesFromAdmin()
    {
        var player = TestData.Player("steam-1");
        player.IsAdmin = true;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new ChangePlayerAdminStatusCommandHandler(_context, NullLogger<ChangePlayerAdminStatusCommandHandler>.Instance);

        await handler.Handle(new ChangePlayerAdminStatusCommand { PlayerId = player.Id, IsAdmin = false });
        await _context.SaveChangesAsync();

        Assert.That(_context.Players.Single().IsAdmin, Is.False);
    }

    [Test]
    public async Task ChangeAdminStatus_UnknownPlayer_DoesNotThrow()
    {
        var handler = new ChangePlayerAdminStatusCommandHandler(_context, NullLogger<ChangePlayerAdminStatusCommandHandler>.Instance);

        Assert.DoesNotThrowAsync(() => handler.Handle(new ChangePlayerAdminStatusCommand { PlayerId = "player_missing", IsAdmin = true }));
    }

    // ── SteamLinkHandler ──────────────────────────────────────────────────────

    [Test]
    public async Task SteamLinked_ExistingPlayer_LinksUserIdAndReturnsNull()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new SteamLinkHandler(_context, NullLogger<SteamLinkHandler>.Instance);

        var result = await handler.Handle(new SteamLinkedEvent { SteamId = "steam-1", UserId = "usr_new" });
        await _context.SaveChangesAsync();

        Assert.That(result, Is.Null, "An existing player must not trigger a CreatePlayerCommand");
        Assert.That(_context.Players.Single().UserId, Is.EqualTo("usr_new"));
    }

    [Test]
    public async Task SteamLinked_NoExistingPlayer_ReturnsCreatePlayerCommand()
    {
        var handler = new SteamLinkHandler(_context, NullLogger<SteamLinkHandler>.Instance);

        var result = await handler.Handle(new SteamLinkedEvent { SteamId = "steam-unknown", UserId = "usr_new" });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SteamId, Is.EqualTo("steam-unknown"));
        Assert.That(result.UserId, Is.EqualTo("usr_new"));
    }

    [Test]
    public async Task SteamUnlinked_ExistingPlayer_ClearsUserId()
    {
        var player = TestData.Player("steam-1");
        player.LinkUserId("usr_1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new SteamLinkHandler(_context, NullLogger<SteamLinkHandler>.Instance);

        await handler.Handle(new SteamUnlinkedEvent { SteamId = "steam-1", UserId = "usr_1" });
        await _context.SaveChangesAsync();

        Assert.That(_context.Players.Single().UserId, Is.Null);
    }

    [Test]
    public async Task SteamUnlinked_UnknownPlayer_DoesNotThrow()
    {
        var handler = new SteamLinkHandler(_context, NullLogger<SteamLinkHandler>.Instance);

        Assert.DoesNotThrowAsync(() => handler.Handle(new SteamUnlinkedEvent { SteamId = "steam-unknown", UserId = "usr_1" }));
    }

    // ── CreateSkinCommandHandler ──────────────────────────────────────────────

    [Test]
    public async Task CreateSkin_KnownPlayer_AddsSkinAndReturnsItsId()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new CreateSkinCommandHandler(_context);

        var response = await handler.Handle(new CreateSkinCommand
        {
            Parameter = new CreateSkinParams { PlayerId = player.Id, Customizer = new SkinCustomizer() }
        });
        await _context.SaveChangesAsync();

        Assert.That(response.Errors, Is.Empty);
        Assert.That(response.SkinId, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task CreateSkin_PlayerAlreadyHasASkin_ReplacesItRatherThanAddingASecond()
    {
        var player = TestData.Player("steam-1");
        player.AddSkin(new CreateSkinParams { PlayerId = player.Id, Customizer = new SkinCustomizer() });
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new CreateSkinCommandHandler(_context);

        await handler.Handle(new CreateSkinCommand
        {
            Parameter = new CreateSkinParams { PlayerId = player.Id, Customizer = new SkinCustomizer() }
        });
        await _context.SaveChangesAsync();

        Assert.That(_context.Skins.Count(s => s.PlayerId == player.Id), Is.EqualTo(1),
            "A player may only have one skin at a time - creating a new one must clear the old one");
    }

    [Test]
    public async Task CreateSkin_UnknownPlayer_ReturnsValidationError()
    {
        var handler = new CreateSkinCommandHandler(_context);

        var response = await handler.Handle(new CreateSkinCommand
        {
            Parameter = new CreateSkinParams { PlayerId = "player_missing", Customizer = new SkinCustomizer() }
        });

        Assert.That(response.Errors, Is.Not.Empty);
    }

    // ── WipeDeployedSlotsCommandHandler ───────────────────────────────────────

    [Test]
    public void Handle_UserDiedEvent_ProducesWipeCommandForThatSteamId()
    {
        var command = WipeDeployedSlotsCommandHandler.Handle(new UserDiedOnIsleServerEvent { SteamId = "steam-1" });

        Assert.That(command.SteamId, Is.EqualTo("steam-1"));
    }

    [Test]
    public async Task WipeDeployedSlots_PlayerWithNoDeployedSlot_DoesNothing()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new WipeDeployedSlotsCommandHandler(_context, NullLogger<WipeDeployedSlotsCommandHandler>.Instance);

        Assert.DoesNotThrowAsync(() => handler.Handle(new WipeDeployedSlotsCommand { SteamId = "steam-1" }));
    }

    [Test]
    public async Task WipeDeployedSlots_PlayerWithDeployedSlot_WipesIt()
    {
        var player = TestData.Player("steam-1");
        var slot = StorageSlot.Create(new CreateStorageSlotParams
        {
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            HealthData = new DinoHealthData(),
            Mutations = new MutationsData(),
        });
        slot.IsDeployed = true;
        slot.StorageId = player.Storage.Id;
        player.Storage.Slots.Add(slot);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        var handler = new WipeDeployedSlotsCommandHandler(_context, NullLogger<WipeDeployedSlotsCommandHandler>.Instance);

        await handler.Handle(new WipeDeployedSlotsCommand { SteamId = "steam-1" });
        // The handler only mutates the tracked entity; Wolverine's transaction middleware is what
        // commits it in production. This SQLite unit test has no such middleware, so it saves itself.
        await _context.SaveChangesAsync();

        // Storage.WipeDeployed() removes the deployed slot outright (frees the capacity for a
        // new dino), it does not merely flip IsDeployed back to false.
        Assert.That(_context.StorageSlots.Any(s => s.Id == slot.Id), Is.False);
    }

    [Test]
    public void WipeDeployedSlots_UnknownPlayer_DoesNotThrow()
    {
        var handler = new WipeDeployedSlotsCommandHandler(_context, NullLogger<WipeDeployedSlotsCommandHandler>.Instance);

        Assert.DoesNotThrowAsync(() => handler.Handle(new WipeDeployedSlotsCommand { SteamId = "steam-missing" }));
    }
}
