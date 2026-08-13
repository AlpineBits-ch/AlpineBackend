using Isle.Api.Endpoints;
using Isle.Api.Services.Quests;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>
/// Which skin a player wears, and the two things that must not change while fixing it: the bounty
/// marker still wins, and a player who has never pressed equip keeps wearing what they were already
/// wearing.
/// </summary>
[TestFixture]
public class SkinSelectionTests
{
    private TestIsleContext _context = null!;
    private BountyRegistry _bounties = null!;
    private SkinStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bounties = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);

        // SkinStore resolves its own scope, so hand it one that returns this context.
        var services = new ServiceCollection();
        services.AddSingleton<Isle.Infrastructure.Persistence.MicroserviceContext>(_context);
        _store = new SkinStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _bounties);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Skin MakeSkin(string playerId, string name, DateTimeOffset createdAt, bool equipped = false)
    {
        var skin = Skin.Create(new CreateSkinParams
        {
            PlayerId = playerId,
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Name = name,
            Customizer = SkinCustomizer.FromProps($"body={ColourFor(name)}"),
        });

        skin.CreatedAt = createdAt;
        skin.UpdatedAt = createdAt;
        skin.IsEquipped = equipped;
        return skin;
    }

    /// <summary>A per-name body colour, so a test can tell which skin came back.</summary>
    private static string ColourFor(string name) => name switch
    {
        "old" => "111111",
        "new" => "222222",
        "chosen" => "333333",
        _ => "444444",
    };

    private static double R(SkinCustomizer? customizer) => customizer?.BodyColor?.R ?? -1;

    private async Task<Player> AddPlayerAsync(string steamId = "steam-1", string? userId = null)
    {
        var player = TestData.Player(steamId);
        player.UserId = userId;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    // ── SkinSelection ─────────────────────────────────────────────────────

    [Test]
    public void TheEquippedSkinWinsOverANewerUnequippedOne()
    {
        var now = DateTimeOffset.UtcNow;
        var chosen = MakeSkin("p1", "chosen", now.AddDays(-10), equipped: true);
        var newer = MakeSkin("p1", "new", now);

        Assert.That(SkinSelection.Effective([newer, chosen])?.Name, Is.EqualTo("chosen"));
    }

    [Test]
    public void WithNothingEquippedTheNewestWins()
    {
        // The pre-existing behaviour, preserved deliberately: this is what Skins.LastOrDefault()
        // resolved to, so nobody's appearance changes on deploy.
        var now = DateTimeOffset.UtcNow;

        var effective = SkinSelection.Effective([MakeSkin("p1", "old", now.AddDays(-1)), MakeSkin("p1", "new", now)]);

        Assert.That(effective?.Name, Is.EqualTo("new"));
    }

    [Test]
    public void TwoEquippedSkinsResolveToOneAnswerAndTheSameOneEveryTime()
    {
        // Only reachable from a partially-written state, but it must not be ambiguous when it happens.
        var at = DateTimeOffset.UtcNow;
        var a = MakeSkin("p1", "a", at, equipped: true);
        var b = MakeSkin("p1", "b", at, equipped: true);

        var first = SkinSelection.Effective([a, b]);
        var second = SkinSelection.Effective([b, a]);

        Assert.That(first!.Id, Is.EqualTo(second!.Id));
    }

    [Test]
    public void NoSkinsAtAllResolvesToNull()
    {
        Assert.That(SkinSelection.Effective([]), Is.Null);
        Assert.That(SkinSelection.Effective(null), Is.Null);
    }

    // ── The backfill, against a pre-existing row ──────────────────────────

    [Test]
    public async Task APreExistingSkinRowWithNoEquippedFlagIsStillWorn()
    {
        // Arranged exactly as an old row looks: created before the column existed, so IsEquipped is
        // false and the name is empty. Nothing migrates it, and nothing needs to.
        var player = await AddPlayerAsync();
        var stale = MakeSkin(player.Id, "old", DateTimeOffset.UtcNow.AddYears(-1));
        stale.IsEquipped = false;
        stale.Name = string.Empty;
        _context.Skins.Add(stale);
        await _context.SaveChangesAsync();

        var effective = await _store.GetAsync("steam-1");

        Assert.That(R(effective), Is.EqualTo(R(stale.Customizer)));
    }

    [Test]
    public async Task AnExplicitEquipBeatsAPreExistingRowForGood()
    {
        var player = await AddPlayerAsync(userId: "user-1");
        var stale = MakeSkin(player.Id, "new", DateTimeOffset.UtcNow);
        var chosen = MakeSkin(player.Id, "chosen", DateTimeOffset.UtcNow.AddYears(-1));
        _context.Skins.AddRange(stale, chosen);
        await _context.SaveChangesAsync();

        // Without the flag the newer row wins; the equip has to invert that.
        Assert.That(R(await _store.GetAsync("steam-1")), Is.EqualTo(R(stale.Customizer)));

        var result = await PlayerProgressionEndpoints.EquipSkin(
            chosen.Id, TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(R(await _store.GetAsync("steam-1")), Is.EqualTo(R(chosen.Customizer)));
    }

    [Test]
    public async Task EquippingClearsTheFlagOnEveryOtherSkin()
    {
        var player = await AddPlayerAsync(userId: "user-1");
        var first = MakeSkin(player.Id, "old", DateTimeOffset.UtcNow.AddDays(-2), equipped: true);
        var second = MakeSkin(player.Id, "new", DateTimeOffset.UtcNow.AddDays(-1), equipped: true);
        var chosen = MakeSkin(player.Id, "chosen", DateTimeOffset.UtcNow);
        _context.Skins.AddRange(first, second, chosen);
        await _context.SaveChangesAsync();

        await PlayerProgressionEndpoints.EquipSkin(
            chosen.Id, TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        var equipped = await _context.Skins.AsNoTracking().Where(skin => skin.IsEquipped).ToListAsync();
        Assert.That(equipped.Select(skin => skin.Id), Is.EqualTo(new[] { chosen.Id }));
    }

    // ── The bounty marker override ────────────────────────────────────────

    [Test]
    public async Task AMarkedPlayerWearsTheMarkerAndNotTheirEquippedSkin()
    {
        // The load-bearing one.
        var player = await AddPlayerAsync();
        var chosen = MakeSkin(player.Id, "chosen", DateTimeOffset.UtcNow, equipped: true);
        _context.Skins.Add(chosen);
        await _context.SaveChangesAsync();

        await _bounties.MarkAsync(new BountyMark(
            "qi-1", player.Id, "steam-1", IsleBridge.Sdk.Species.Tyrannosaurus,
            DateTimeOffset.UtcNow.AddHours(1)));

        var effective = await _store.GetAsync("steam-1");

        Assert.Multiple(() =>
        {
            Assert.That(R(effective), Is.Not.EqualTo(R(chosen.Customizer)));
            Assert.That(R(effective), Is.EqualTo(R(BountyMarkerSkin.For(IsleBridge.Sdk.Species.Tyrannosaurus))));
        });
    }

    [Test]
    public async Task OnceTheMarkIsGoneTheEquippedSkinComesBack()
    {
        var player = await AddPlayerAsync();
        var chosen = MakeSkin(player.Id, "chosen", DateTimeOffset.UtcNow, equipped: true);
        _context.Skins.Add(chosen);
        await _context.SaveChangesAsync();

        await _bounties.MarkAsync(new BountyMark(
            "qi-1", player.Id, "steam-1", IsleBridge.Sdk.Species.Tyrannosaurus,
            DateTimeOffset.UtcNow.AddHours(1)));
        await _bounties.UnmarkAsync("steam-1");

        Assert.That(R(await _store.GetAsync("steam-1")), Is.EqualTo(R(chosen.Customizer)));
    }

    // ── Negative ──────────────────────────────────────────────────────────

    [Test]
    public async Task APlayerWithNoSkinsResolvesToNothingRatherThanThrowing()
    {
        await AddPlayerAsync();

        Assert.That(await _store.GetAsync("steam-1"), Is.Null);
    }

    [Test]
    public async Task AnUnknownSteamIdResolvesToNothing()
    {
        Assert.That(await _store.GetAsync("nobody"), Is.Null);
    }

    [Test]
    public async Task EquippingSomebodyElsesSkinIsRefused()
    {
        var mine = await AddPlayerAsync("steam-1", userId: "user-1");
        var theirs = await AddPlayerAsync("steam-2");

        var hostile = MakeSkin(theirs.Id, "chosen", DateTimeOffset.UtcNow);
        _context.Skins.Add(hostile);
        _context.Skins.Add(MakeSkin(mine.Id, "old", DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();

        var result = await PlayerProgressionEndpoints.EquipSkin(
            hostile.Id, TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
        Assert.That(await _context.Skins.AsNoTracking().AnyAsync(skin => skin.IsEquipped), Is.False);
    }

    [Test]
    public async Task EquippingFromAnAccountWithNoPlayerRowIsRefused()
    {
        var result = await PlayerProgressionEndpoints.EquipSkin(
            "skin-nope", TestPrincipal.Create("user-with-no-player"), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task APlayerCanNameTheirOwnSkin()
    {
        var player = await AddPlayerAsync(userId: "user-1");
        var skin = MakeSkin(player.Id, "old", DateTimeOffset.UtcNow);
        _context.Skins.Add(skin);
        await _context.SaveChangesAsync();

        await PlayerProgressionEndpoints.RenameSkin(
            skin.Id, new RenameSkinDto { Name = "Ashen" },
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That((await _context.Skins.AsNoTracking().FirstAsync(s => s.Id == skin.Id)).Name, Is.EqualTo("Ashen"));
    }

    [Test]
    public async Task ClearingASkinsNameResetsItToTheSpecies()
    {
        var player = await AddPlayerAsync(userId: "user-1");
        var skin = MakeSkin(player.Id, "old", DateTimeOffset.UtcNow);
        _context.Skins.Add(skin);
        await _context.SaveChangesAsync();

        await PlayerProgressionEndpoints.RenameSkin(
            skin.Id, new RenameSkinDto { Name = "  " },
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That((await _context.Skins.AsNoTracking().FirstAsync(s => s.Id == skin.Id)).Name,
            Is.EqualTo(IsleBridge.Sdk.Species.FriendlyName(IsleBridge.Sdk.Species.Tyrannosaurus)));
    }

    [Test]
    public async Task RenamingSomebodyElsesSkinIsRefused()
    {
        await AddPlayerAsync("steam-1", userId: "user-1");
        var theirs = await AddPlayerAsync("steam-2");
        var hostile = MakeSkin(theirs.Id, "chosen", DateTimeOffset.UtcNow);
        _context.Skins.Add(hostile);
        await _context.SaveChangesAsync();

        var result = await PlayerProgressionEndpoints.RenameSkin(
            hostile.Id, new RenameSkinDto { Name = "Mine now" },
            TestPrincipal.Create("user-1"), _context, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
        Assert.That((await _context.Skins.AsNoTracking().FirstAsync(s => s.Id == hostile.Id)).Name,
            Is.Not.EqualTo("Mine now"));
    }

    [Test]
    public void ASkinCreatedWithNoNameFallsBackToItsSpecies()
    {
        var skin = Skin.Create(new CreateSkinParams
        {
            PlayerId = "p1",
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Name = "   ",
            Customizer = new SkinCustomizer(),
        });

        Assert.That(skin.Name, Is.EqualTo(IsleBridge.Sdk.Species.FriendlyName(IsleBridge.Sdk.Species.Tyrannosaurus)));
    }

    [Test]
    public void AnOverlongSkinNameIsTruncatedRatherThanRefused()
    {
        var name = new string('x', Skin.MaxNameLength + 40);

        Assert.That(Skin.ResolveName(name, IsleBridge.Sdk.Species.Tyrannosaurus),
            Has.Length.EqualTo(Skin.MaxNameLength));
    }
}
