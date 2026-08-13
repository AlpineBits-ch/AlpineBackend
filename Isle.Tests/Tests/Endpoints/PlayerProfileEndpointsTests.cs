using Isle.Api.Endpoints;
using Isle.Api.Services.State;
using Isle.Domain.Entity;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers PlayerProfileEndpoints (/me, /me/storage, /me/skins) - static, Wolverine-attributed and
/// authorized, called here as plain C# methods with a TestIsleContext and a Redis-backed
/// PlayerPresenceManager.
/// </summary>
[TestFixture]
public class PlayerProfileEndpointsTests
{
    private TestIsleContext _context = null!;
    private PlayerPresenceManager _presence = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── /me ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Me_NoNameIdentifierClaim_ReturnsUnauthorized()
    {
        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.CreateAnonymous(), _context, _presence, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Me_SignedInButNeverLinkedSteam_ReturnsLinkedFalseRatherThanNotFound()
    {
        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-without-steam"), _context, _presence, CancellationToken.None);

        var dto = Value<IsleProfileDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(dto.Linked, Is.False);
            Assert.That(dto.SteamId, Is.Null);
            Assert.That(dto.FriendlyId, Is.Null);
            Assert.That(dto.MemberSince, Is.Null);
        });
    }

    [Test]
    public async Task Me_LinkedPlayer_ReturnsTheProfile()
    {
        var player = TestData.Player("steam-1", "Rexy");
        player.UserId = "user-1";
        player.IsAdmin = true;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-1"), _context, _presence, CancellationToken.None);

        var dto = Value<IsleProfileDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Linked, Is.True);
            // The caller's own Steam ID on their own authorized endpoint discloses nothing they did
            // not supply themselves - unlike the anonymous roster, where it may never appear.
            Assert.That(dto.SteamId, Is.EqualTo("steam-1"));
            Assert.That(dto.InGameName, Is.EqualTo("Rexy"));
            Assert.That(dto.FriendlyId, Is.EqualTo(player.FriendlyId));
            Assert.That(dto.MemberSince, Is.EqualTo(player.CreatedAt));
            Assert.That(dto.Xp, Is.EqualTo(player.Xp));
            Assert.That(dto.IsAdmin, Is.True);
        });
    }

    [Test]
    public async Task Me_LinkedSteamButNeverRanLinkInGame_ReturnsANullInGameName()
    {
        // Linking Steam and telling the service your in-game name are two separate steps.
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-1"), _context, _presence, CancellationToken.None);

        var dto = Value<IsleProfileDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Linked, Is.True);
            Assert.That(dto.InGameName, Is.Null);
        });
    }

    [Test]
    public async Task Me_PlayerIsInTheWorld_ReportsIsInGame()
    {
        var player = TestData.Player("steam-1", "Rexy");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        await _presence.AddPlayerIdAsync(player.Id);

        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-1"), _context, _presence, CancellationToken.None);

        Assert.That(Value<IsleProfileDto>(result).IsInGame, Is.True);
    }

    [Test]
    public async Task Me_PlayerIsOffline_ReportsIsInGameFalse()
    {
        var player = TestData.Player("steam-1", "Rexy");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-1"), _context, _presence, CancellationToken.None);

        Assert.That(Value<IsleProfileDto>(result).IsInGame, Is.False);
    }

    [Test]
    public async Task Me_AnotherPlayersUserId_DoesNotResolveToThisPlayer()
    {
        var player = TestData.Player("steam-1", "Rexy");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Me(
            TestPrincipal.Create("user-2"), _context, _presence, CancellationToken.None);

        Assert.That(Value<IsleProfileDto>(result).Linked, Is.False);
    }

    // ── /me/storage ───────────────────────────────────────────────────────

    [Test]
    public async Task Storage_NoNameIdentifierClaim_ReturnsUnauthorized()
    {
        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.CreateAnonymous(), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Storage_NeverLinkedSteam_ReturnsLinkedFalseButStillServesTheCosts()
    {
        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-without-steam"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(dto.Linked, Is.False);
            Assert.That(dto.Slots, Is.Empty);
            Assert.That(dto.MaxSlotCount, Is.EqualTo(0));
            // Game constants, not user data - the empty state can still show what a slot costs.
            Assert.That(dto.SlotPurchaseCost, Is.EqualTo(Isle.Domain.Aggregates.Storage.SlotPurchaseCost));
            Assert.That(dto.GrowthThreshold, Is.EqualTo(Isle.Domain.Aggregates.Storage.GrowthThreshold));
        });
    }

    [Test]
    public async Task Storage_LinkedPlayerWithNoStorageRow_ReportsLinkedWithZeroCapacity()
    {
        var player = TestData.Player("steam-1");
        player.UserId = "user-1";
        player.Storage = null!;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Linked, Is.True);
            Assert.That(dto.MaxSlotCount, Is.EqualTo(0));
            Assert.That(dto.Slots, Is.Empty);
        });
    }

    [Test]
    public async Task Storage_EmptyStorage_ReportsZeroOfTheDefaultFive()
    {
        await LinkedPlayerAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.UsedSlotCount, Is.EqualTo(0));
            // The site hard-coded six; the real default is five and it is purchasable.
            Assert.That(dto.MaxSlotCount, Is.EqualTo(5));
            Assert.That(dto.Slots, Is.Empty);
        });
    }

    [Test]
    public async Task Storage_PartiallyFull_ReportsUsedAgainstMax()
    {
        var player = await LinkedPlayerAsync();
        Store(player, "Tyrannosaurus", 0.6);
        Store(player, "Triceratops", 0.9);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.UsedSlotCount, Is.EqualTo(2));
            Assert.That(dto.MaxSlotCount, Is.EqualTo(5));
            Assert.That(dto.Slots, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task Storage_Full_ReportsUsedEqualToMax()
    {
        var player = await LinkedPlayerAsync();
        for (var i = 0; i < 5; i++)
            Store(player, "Tyrannosaurus", 0.6);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.That(dto.UsedSlotCount, Is.EqualTo(dto.MaxSlotCount));
    }

    [Test]
    public async Task Storage_PurchasedSlot_ReportsTheWidenedCapacity()
    {
        // The whole reason MaxSlotCount is served rather than assumed.
        var player = await LinkedPlayerAsync();
        player.Storage.PurchaseSlot();
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That(Value<IsleStorageDto>(result).MaxSlotCount, Is.EqualTo(6));
    }

    [Test]
    public async Task Storage_MapsEverySlotField()
    {
        var player = await LinkedPlayerAsync();
        var slot = Store(player, IsleBridge.Sdk.Species.Tyrannosaurus, 0.8,
            new DinoHealthData { Health = 91, Hunger = 82, Thirst = 73, Stamina = 64 });
        player.Storage.MarkDeployed(slot.Id);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result).Slots.Single();
        Assert.Multiple(() =>
        {
            Assert.That(dto.Id, Is.EqualTo(slot.Id));
            Assert.That(dto.Species, Is.EqualTo(slot.FriendlySpeciesName()));
            Assert.That(dto.Growth, Is.EqualTo(0.8));
            Assert.That(dto.IsDeployed, Is.True);
            Assert.That(dto.StoredAt, Is.EqualTo(slot.CreatedAt));
            Assert.That(dto.Health!.Health, Is.EqualTo(91));
            Assert.That(dto.Health.Hunger, Is.EqualTo(82));
            Assert.That(dto.Health.Thirst, Is.EqualTo(73));
            Assert.That(dto.Health.Stamina, Is.EqualTo(64));
        });
    }

    [Test]
    public async Task Storage_AnotherPlayersStorage_IsNotReturned()
    {
        var player = await LinkedPlayerAsync();
        Store(player, "Tyrannosaurus", 0.6);
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Storage(
            TestPrincipal.Create("user-2"), _context, CancellationToken.None);

        var dto = Value<IsleStorageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Linked, Is.False);
            Assert.That(dto.Slots, Is.Empty);
        });
    }

    // ── /me/skins ─────────────────────────────────────────────────────────

    [Test]
    public async Task Skins_NoNameIdentifierClaim_ReturnsUnauthorized()
    {
        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.CreateAnonymous(), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Skins_NeverLinkedSteam_ReturnsLinkedFalseRatherThanNotFound()
    {
        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.Create("user-without-steam"), _context, CancellationToken.None);

        var dto = Value<IsleSkinsDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(dto.Linked, Is.False);
            Assert.That(dto.Skins, Is.Empty);
        });
    }

    [Test]
    public async Task Skins_LinkedPlayerWithNoSkins_ReturnsAnEmptyList()
    {
        await LinkedPlayerAsync();

        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var dto = Value<IsleSkinsDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Linked, Is.True);
            Assert.That(dto.Skins, Is.Empty);
        });
    }

    [Test]
    public async Task Skins_SingleSkin_IsTheEffectiveOne()
    {
        var player = await LinkedPlayerAsync();
        player.AddSkin(new CreateSkinParams
        {
            PlayerId = player.Id,
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Customizer = SkinCustomizer.FromProps("body=FF0000"),
        });
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var skin = Value<IsleSkinsDto>(result).Skins.Single();
        Assert.Multiple(() =>
        {
            Assert.That(skin.Species, Is.EqualTo(IsleBridge.Sdk.Species.FriendlyName(IsleBridge.Sdk.Species.Tyrannosaurus)));
            Assert.That(skin.IsEffective, Is.True);
            Assert.That(skin.Customizer, Is.Not.Null);
            Assert.That(skin.Customizer!.BodyColor!.R, Is.EqualTo(1.0).Within(0.001));
        });
    }

    [Test]
    public async Task Skins_MultipleSkins_MarksExactlyTheOneSkinStoreWouldReapply()
    {
        var player = await LinkedPlayerAsync();
        player.AddSkin(new CreateSkinParams { PlayerId = player.Id, Species = IsleBridge.Sdk.Species.Tyrannosaurus, Customizer = SkinCustomizer.FromProps("body=FF0000") });
        await Task.Delay(5);
        player.AddSkin(new CreateSkinParams { PlayerId = player.Id, Species = IsleBridge.Sdk.Species.Triceratops, Customizer = SkinCustomizer.FromProps("body=00FF00") });
        await _context.SaveChangesAsync();

        // SkinStore.GetAsync picks Skins.LastOrDefault() off exactly this query shape, with no
        // ordering of its own.
        var expectedId = (await _context.Players.AsNoTracking().Include(p => p.Skins)
            .FirstAsync(p => p.UserId == "user-1")).Skins.LastOrDefault()!.Id;

        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var skins = Value<IsleSkinsDto>(result).Skins;
        Assert.Multiple(() =>
        {
            Assert.That(skins, Has.Count.EqualTo(2));
            Assert.That(skins.Count(s => s.IsEffective), Is.EqualTo(1));
            Assert.That(skins.Single(s => s.IsEffective).Id, Is.EqualTo(expectedId));
        });
    }

    [Test]
    public async Task Skins_AnotherPlayersSkins_AreNotReturned()
    {
        var player = await LinkedPlayerAsync();
        player.AddSkin(new CreateSkinParams { PlayerId = player.Id, Species = IsleBridge.Sdk.Species.Tyrannosaurus, Customizer = SkinCustomizer.FromProps("body=FF0000") });
        await _context.SaveChangesAsync();

        var result = await PlayerProfileEndpoints.Skins(
            TestPrincipal.Create("user-2"), _context, CancellationToken.None);

        Assert.That(Value<IsleSkinsDto>(result).Skins, Is.Empty);
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    private async Task<Isle.Domain.Aggregates.Player> LinkedPlayerAsync()
    {
        var player = TestData.Player("steam-1", "Rexy");
        player.UserId = "user-1";
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private static StorageSlot Store(
        Isle.Domain.Aggregates.Player player, string species, double growth, DinoHealthData? health = null) =>
        player.Storage.StoreDino(new CreateStorageSlotParams
        {
            Species = species,
            Growth = growth,
            HealthData = health ?? new DinoHealthData { Health = 100, Hunger = 100, Thirst = 100, Stamina = 100 },
            Mutations = new MutationsData(),
        });

    private static T Value<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private static int? StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;
}
