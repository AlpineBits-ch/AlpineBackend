using System.Numerics;
using System.Text.Json;
using Isle.Api.Endpoints;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Endpoints;

/// <summary>
/// Covers ServerStatusEndpoints - the anonymous server/roster surface, called here as plain C#
/// methods with a real WorldRosterCache, a Redis-backed PlayerPresenceManager and a
/// FakeDistributedCache behind PlayerSpawnTracker.
/// </summary>
[TestFixture]
public class ServerStatusEndpointsTests
{
    private WorldRosterCache _roster = null!;
    private PlayerPresenceManager _presence = null!;
    private FakeDistributedCache _cache = null!;
    private PlayerSpawnTracker _spawns = null!;

    [SetUp]
    public void SetUp()
    {
        _roster = new WorldRosterCache();
        _presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _cache = new FakeDistributedCache();
        _spawns = new PlayerSpawnTracker(_cache);
    }

    // ── Status ────────────────────────────────────────────────────────────

    [Test]
    public void Status_NoRosterReadHasEverLanded_ReportsZeroAndStale()
    {
        var result = ServerStatusEndpoints.Status(_roster, _presence);

        Assert.Multiple(() =>
        {
            Assert.That(result.OnlinePlayerCount, Is.EqualTo(0));
            Assert.That(result.RosterLastUpdatedAt, Is.Null);
            // Zero players and "we cannot reach the server" must not look the same to the site.
            Assert.That(result.RosterIsStale, Is.True);
            Assert.That(result.RosterStalenessThresholdSeconds, Is.EqualTo(90));
        });
    }

    [Test]
    public void Status_FreshRoster_ReportsTheHeadcountAsLive()
    {
        _roster.Replace([Entry("steam-1", "Rexy"), Entry("steam-2", "Trikey")]);

        var result = ServerStatusEndpoints.Status(_roster, _presence);

        Assert.Multiple(() =>
        {
            Assert.That(result.OnlinePlayerCount, Is.EqualTo(2));
            Assert.That(result.RosterIsStale, Is.False);
            Assert.That(result.RosterLastUpdatedAt, Is.Not.Null);
        });
    }

    [Test]
    public void Status_RosterOlderThanTheThreshold_KeepsTheCountButFlagsItStale()
    {
        // WorldRosterService deliberately leaves the previous snapshot in place when an RCON read
        // fails, so the count stays populated and only this flag separates it from a live one.
        _roster.Replace([Entry("steam-1", "Rexy")]);
        _roster.Aged(TimeSpan.FromMinutes(30));

        var result = ServerStatusEndpoints.Status(_roster, _presence);

        Assert.Multiple(() =>
        {
            Assert.That(result.OnlinePlayerCount, Is.EqualTo(1));
            Assert.That(result.RosterIsStale, Is.True);
        });
    }

    [Test]
    public void Status_RosterJustInsideTheThreshold_IsNotStale()
    {
        _roster.Replace([Entry("steam-1", "Rexy")]);
        _roster.Aged(TimeSpan.FromSeconds(60));

        Assert.That(ServerStatusEndpoints.Status(_roster, _presence).RosterIsStale, Is.False);
    }

    [Test]
    public async Task Status_CountsLinkedPlayersSeparatelyFromTheWholeRoster()
    {
        // The roster is every player on the server; presence only knows the ones with a Venta
        // account. They are different numbers on purpose.
        _roster.Replace([Entry("steam-1", "Rexy"), Entry("steam-2", "Trikey"), Entry("steam-3", "Stego")]);
        await _presence.AddPlayerIdAsync("player_1");

        var result = ServerStatusEndpoints.Status(_roster, _presence);

        Assert.Multiple(() =>
        {
            Assert.That(result.OnlinePlayerCount, Is.EqualTo(3));
            Assert.That(result.LinkedPlayersInGame, Is.EqualTo(1));
        });
    }

    [Test]
    public void Status_ExposesNeitherSteamIdsNorCoordinates()
    {
        _roster.Replace([Entry("76561198_secret", "Rexy")]);

        var serialized = JsonSerializer.Serialize(ServerStatusEndpoints.Status(_roster, _presence));

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Does.Not.Contain("76561198_secret"));
            Assert.That(serialized, Does.Not.Contain("314159"));
            Assert.That(serialized, Does.Not.Contain("Secret Hollow"));
        });
    }

    // ── Roster ────────────────────────────────────────────────────────────

    [Test]
    public async Task Roster_NobodyOnline_ReturnsNoPlayers()
    {
        _roster.Replace([]);

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Players, Is.Empty);
            Assert.That(result.IsStale, Is.False);
            Assert.That(result.LastUpdatedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Roster_NoReadHasEverLanded_ReturnsEmptyAndStale()
    {
        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Players, Is.Empty);
            Assert.That(result.IsStale, Is.True);
            Assert.That(result.LastUpdatedAt, Is.Null);
        });
    }

    [Test]
    public async Task Roster_StaleCache_StillReturnsTheLastKnownPlayersButFlagsThem()
    {
        _roster.Replace([Entry("steam-1", "Rexy")]);
        _roster.Aged(TimeSpan.FromMinutes(30));

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Players, Has.Count.EqualTo(1));
            Assert.That(result.IsStale, Is.True);
        });
    }

    [Test]
    public async Task Roster_MapsNameSpeciesAndGrowth()
    {
        _roster.Replace([Entry("steam-1", "Rexy", species: "Tyrannosaurus", growth: 0.75f)]);

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        var entry = result.Players.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Player, Is.EqualTo("Rexy"));
            Assert.That(entry.Species, Is.EqualTo("Tyrannosaurus"));
            Assert.That(entry.Growth, Is.EqualTo(0.75f));
        });
    }

    [Test]
    public async Task Roster_GameServerReportedABlankName_FallsBackToUnknownPlayerRatherThanTheSteamId()
    {
        _roster.Replace([Entry("76561198_secret", name: "  ")]);

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        Assert.That(result.Players.Single().Player, Is.EqualTo("Unknown player"));
    }

    [Test]
    public async Task Roster_SpawnTimestampKnown_ReportsTimeAlive()
    {
        _roster.Replace([Entry("steam-1", "Rexy")]);
        await _spawns.MarkSpawnedAsync("steam-1");

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        var entry = result.Players.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.SpawnedAt, Is.Not.Null);
            Assert.That(entry.TimeAliveSeconds, Is.Not.Null.And.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task Roster_NoSpawnTimestamp_LeavesTimeAliveNullRatherThanGuessingZero()
    {
        // The tracker's entries expire after two hours, so a long-lived dino genuinely has no
        // timestamp - reporting that as "0 seconds alive" would be a lie in the wrong direction.
        _roster.Replace([Entry("steam-1", "Rexy")]);

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        var entry = result.Players.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.SpawnedAt, Is.Null);
            Assert.That(entry.TimeAliveSeconds, Is.Null);
        });
    }

    [Test]
    public async Task Roster_ResolvesEachPlayersOwnSpawnTimestamp()
    {
        _roster.Replace([Entry("steam-1", "Rexy"), Entry("steam-2", "Trikey")]);
        await _spawns.MarkSpawnedAsync("steam-2");

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Players.Single(p => p.Player == "Rexy").SpawnedAt, Is.Null);
            Assert.That(result.Players.Single(p => p.Player == "Trikey").SpawnedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Roster_ExposesNeitherSteamIdsNorPositions()
    {
        // A Steam ID resolves to a public Steam profile, and a position (or the region/location name
        // that stands in for one) lets a web page follow an individual player around the island.
        // Both are read internally here - the Steam ID keys the spawn lookup - and neither may leave.
        _roster.Replace([Entry("76561198_secret", "Rexy")]);
        await _spawns.MarkSpawnedAsync("76561198_secret");

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Multiple(() =>
        {
            Assert.That(serialized, Does.Not.Contain("76561198_secret"));
            Assert.That(serialized, Does.Not.Contain("314159"), "world X leaked");
            Assert.That(serialized, Does.Not.Contain("271828"), "world Y leaked");
            Assert.That(serialized, Does.Not.Contain("secret-hollow"), "region id leaked");
            Assert.That(serialized, Does.Not.Contain("Secret Hollow"), "location name leaked");
        });
    }

    [Test]
    public async Task Roster_EntryCarriesOnlyTheFourPublicFields()
    {
        // Stronger than the string search above: this fails the moment anyone widens the DTO, which
        // is the only way the fields excluded above come back by accident.
        _roster.Replace([Entry("76561198_secret", "Rexy")]);

        var result = await ServerStatusEndpoints.Roster(_roster, _spawns, CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var names = document.RootElement.GetProperty("Players")[0]
            .EnumerateObject().Select(p => p.Name).ToArray();

        Assert.That(names, Is.EquivalentTo(new[] { "Player", "Species", "Growth", "TimeAliveSeconds", "SpawnedAt" }));
    }

    private static RosterEntry Entry(string steam, string name, string species = "Tyrannosaurus", float growth = 0.5f) =>
        new(Steam: steam,
            Name: name,
            Species: species,
            Growth: growth,
            // Distinctive enough that a substring search cannot false-positive on an ordinary number.
            Position: new Vector3(314159f, -271828f, 42f),
            RegionId: "secret-hollow",
            LocationName: "Secret Hollow");
}

file static class WorldRosterCacheTestExtensions
{
    /// <summary>
    /// Backdates the cache's LastUpdatedAt so staleness can be exercised without waiting.
    /// </summary>
    public static void Aged(this WorldRosterCache cache, TimeSpan by)
    {
        typeof(WorldRosterCache)
            .GetProperty(nameof(WorldRosterCache.LastUpdatedAt))!
            .SetValue(cache, DateTimeOffset.UtcNow - by);
    }
}
