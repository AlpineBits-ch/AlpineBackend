using System.Numerics;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.World;
using Isle.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillDirectorTests
{
    private TestIsleContext _context = null!;
    private WorldRosterCache _roster = null!;
    private KingOfTheHillDirector _director = null!;

    private static readonly Vector3 ZoneCenter = new(1000, 1000, 0);

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _roster = new WorldRosterCache();
        _director = new KingOfTheHillDirector(_context, _roster, NullLogger<KingOfTheHillDirector>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static RosterEntry Entry(string steam, Vector3 position) =>
        new(steam, "Test", "Rex", 1.0f, position, RegionId: null, LocationName: "Somewhere");

    private void FreshRoster(params RosterEntry[] entries) => _roster.Replace(entries);

    [Test]
    public async Task ChooseAsync_RosterNeverRefreshed_ReturnsNull()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 1));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_NoEnabledDefinition_ReturnsNull()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(center: ZoneCenter, enabled: false));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_OnCooldown_ReturnsNull()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            center: ZoneCenter, cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-5)));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_CooldownElapsed_IsEligibleAgain()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            center: ZoneCenter, cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-21)));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Not.Null);
    }

    [Test]
    public async Task ChooseAsync_NobodyInZone_ReturnsNull()
    {
        FreshRoster(Entry("steam_1", new Vector3(999999, 999999, 0)));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 1));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_BelowMinPlayersToTrigger_ReturnsNull()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 2));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_EnoughPlayersInZone_ReturnsTheDefinition()
    {
        FreshRoster(Entry("steam_1", ZoneCenter), Entry("steam_2", ZoneCenter));
        var definition = TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 2);
        _context.GameModeDefinitions.Add(definition);
        await _context.SaveChangesAsync();

        var chosen = await _director.ChooseAsync();

        Assert.That(chosen, Is.Not.Null);
        Assert.That(chosen!.Id, Is.EqualTo(definition.Id));
    }

    [Test]
    public async Task ChooseForcedAsync_BypassesTriggerGate_EvenWithNobodyInZone()
    {
        var definition = TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 5);
        _context.GameModeDefinitions.Add(definition);
        await _context.SaveChangesAsync();

        var chosen = await _director.ChooseForcedAsync();

        Assert.That(chosen, Is.Not.Null);
    }

    [Test]
    public async Task ChooseForcedAsync_StillRespectsCooldown()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-5)));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseForcedAsync(), Is.Null);
    }

    [Test]
    public async Task ChooseAsync_OtherEnabledDefinitionWithADifferentName_IsIgnored()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        var koth = TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 1);
        var other = TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: 1);
        other.DisplayName = "Some Future Mode";
        _context.GameModeDefinitions.AddRange(koth, other);
        await _context.SaveChangesAsync();

        var chosen = await _director.ChooseAsync();

        Assert.That(chosen, Is.Not.Null);
        Assert.That(chosen!.Id, Is.EqualTo(koth.Id));
    }

    [Test]
    public async Task ChooseAsync_NullMinPlayersToTrigger_DefaultsToRequiringOne()
    {
        FreshRoster(Entry("steam_1", ZoneCenter));
        _context.GameModeDefinitions.Add(TestData.KothDefinition(center: ZoneCenter, minPlayersToTrigger: null));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseAsync(), Is.Not.Null);
    }

    [Test]
    public async Task ChooseForcedAsync_DisabledDefinition_ReturnsNull()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(enabled: false));
        await _context.SaveChangesAsync();

        Assert.That(await _director.ChooseForcedAsync(), Is.Null);
    }
}
