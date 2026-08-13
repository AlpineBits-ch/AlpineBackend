using Isle.Api.Endpoints;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Tests.Helpers;
using IsleBridge.Sdk.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>
/// The self-only live dinosaur view: what it answers, and the two identifiers it must never carry.
/// </summary>
[TestFixture]
public class LiveDinoEndpointTests
{
    private TestIsleContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private PlayerVitalsCache _vitals = null!;
    private PlayerSpawnTracker _spawns = null!;
    private WorldRosterCache _roster = null!;
    private RegionMap _regions = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _cache = new FakeDistributedCache();
        _vitals = new PlayerVitalsCache(_cache, NullLogger<PlayerVitalsCache>.Instance);
        _spawns = new PlayerSpawnTracker(_cache);
        _roster = new WorldRosterCache();
        _regions = new RegionMap();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task AddPlayerAsync(string steamId = "steam-1", string userId = "user-1")
    {
        var player = TestData.Player(steamId, "Vex");
        player.UserId = userId;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
    }

    private Task<IResult> CallAsync(string userId = "user-1") =>
        PlayerProgressionEndpoints.Dino(
            TestPrincipal.Create(userId), _context, _vitals, _spawns, _regions, _roster, CancellationToken.None);

    private Task CaptureAsync(string steam = "steam-1") =>
        _vitals.CaptureAsync(new StatsSnapshot
        {
            Steam = steam,
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Growth = 0.9,
            Pos = new Position { X = 123456, Y = 654321, Z = 12 },
            Vitals = new Vitals { Hp = 80, HpMax = 100 },
        });

    // ── Normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task AFreshSnapshotComesBackAsTheCallersDinosaur()
    {
        await AddPlayerAsync();
        await CaptureAsync();

        var dto = (LiveDinoDto)((IValueHttpResult)await CallAsync()).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(dto.Species, Is.EqualTo(IsleBridge.Sdk.Species.FriendlyName(IsleBridge.Sdk.Species.Tyrannosaurus)));
            Assert.That(dto.Growth, Is.EqualTo(0.9));
            Assert.That(dto.Health, Is.EqualTo(0.8));
            Assert.That(dto.Location, Is.Not.Empty);
        });
    }

    [Test]
    public async Task GenderComesFromTheRoster()
    {
        // The bridge's stats feed carries no gender field at all, so this is the only source there is.
        await AddPlayerAsync();
        await CaptureAsync();
        _roster.Replace([new RosterEntry("steam-1", "Vex", "Rex", 0.9f, default, null, "Somewhere", Gender: "Female")]);

        var dto = (LiveDinoDto)((IValueHttpResult)await CallAsync()).Value!;

        Assert.That(dto.Gender, Is.EqualTo("Female"));
    }

    [Test]
    public async Task GenderIsNullUntilARosterReadHasLanded()
    {
        // The roster ticks every 30 seconds and the stats feed every second, so a just-joined player
        // has vitals before they have a gender. Null, not a guess.
        await AddPlayerAsync();
        await CaptureAsync();

        var dto = (LiveDinoDto)((IValueHttpResult)await CallAsync()).Value!;

        Assert.That(dto.Gender, Is.Null);
    }

    // ── Privacy ───────────────────────────────────────────────────────────

    [Test]
    public async Task ThePositionIsAPlaceNameAndTheResponseCarriesNoCoordinatesOrSteamId()
    {
        // The rule QuestEndpoints and KingOfTheHillEndpoints both spell out, applied to the finest
        // grained thing this service holds: where one named person is, right now.
        await AddPlayerAsync("76561198000000000");
        await CaptureAsync("76561198000000000");

        var dto = (LiveDinoDto)((IValueHttpResult)await CallAsync()).Value!;
        var json = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("76561198000000000"));
            Assert.That(json, Does.Not.Contain("123456"));
            Assert.That(json, Does.Not.Contain("654321"));
        });
    }

    [Test]
    public async Task ACallerOnlyEverSeesTheirOwnDinosaur()
    {
        await AddPlayerAsync("steam-1", "user-1");
        await AddPlayerAsync("steam-2", "user-2");
        await CaptureAsync("steam-2");

        // user-1 has no snapshot of their own, and must not inherit user-2's.
        Assert.That(await CallAsync("user-1"), Is.InstanceOf<NoContent>());
    }

    // ── Negative ──────────────────────────────────────────────────────────

    [Test]
    public async Task NoSnapshotAnswersNoContentRatherThanAnEmptyDinosaur()
    {
        // Offline, dead and not respawned, or the feed is down. All ordinary.
        await AddPlayerAsync();

        Assert.That(await CallAsync(), Is.InstanceOf<NoContent>());
    }

    [Test]
    public async Task AnAccountWithNoLinkedPlayerAnswersNoContent()
    {
        Assert.That(await CallAsync("user-with-no-player"), Is.InstanceOf<NoContent>());
    }
}
