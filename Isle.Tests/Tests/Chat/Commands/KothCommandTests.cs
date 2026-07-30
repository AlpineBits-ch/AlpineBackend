using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.KingOfTheHill;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class KothCommandTests
{
    private TestIsleContext _context = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private KothCommand _command = null!;

    private const string InstanceId = "koth_instance_1";

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(), NullLogger<KingOfTheHillMatchStateStore>.Instance);
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);
        _command = new KothCommand(_context, _stateStore, _ledger);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_NoMatchRunningAndNoDefinition_ReturnsGenericIdleMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Is.EqualTo("No King of the Hill match right now. It starts once players enter the zone."));
    }

    [Test]
    public async Task ExecuteAsync_NoMatchRunningAndOnCooldown_ReturnsNextAvailableMessage()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-5)));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("Next available in"));
    }

    [Test]
    public async Task ExecuteAsync_NoMatchRunningAndCooldownElapsed_ReturnsGenericIdleMessage()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-25)));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Is.EqualTo("No King of the Hill match right now. It starts once players enter the zone."));
    }

    [Test]
    public async Task ExecuteAsync_MatchRunningNoContestants_ReturnsContestedZeroCountMessage()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", InstanceId, DateTime.UtcNow, []));

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("nobody has registered a control tick yet"));
        Assert.That(result, Does.Contain("the hill is contested"));
    }

    [Test]
    public async Task ExecuteAsync_MatchRunningOneContestantUnregisteredPlayer_UsesSteamIdAsHolderName()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", InstanceId, DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_solo"]);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("1 player has registered control ticks"));
        Assert.That(result, Does.Contain("steam_solo has held it for"));
    }

    [Test]
    public async Task ExecuteAsync_MatchRunningOneContestantRegisteredPlayer_UsesInGameName()
    {
        var player = TestData.Player("steam_solo", inGameName: "RexKing");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        await _stateStore.WriteAsync(new KothMatchState("def_1", InstanceId, DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_solo"]);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("RexKing has held it for"));
    }

    [Test]
    public async Task ExecuteAsync_MatchRunningMultipleContestantsContested_ReportsCountAndContested()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", InstanceId, DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_a"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_b"]);
        await _ledger.ApplyPresenceAsync(InstanceId, ["steam_a", "steam_b"]); // contests the zone, clears holder

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("2 players have registered control ticks"));
        Assert.That(result, Does.Contain("the hill is contested"));
    }

    [Test]
    public void Name_IsKoth()
    {
        Assert.That(_command.Name, Is.EqualTo("koth"));
    }

    [Test]
    public void IsAdminOnly_IsFalse()
    {
        Assert.That(_command.IsAdminOnly, Is.False);
    }

    [Test]
    public void Cooldown_IsFifteenSeconds()
    {
        Assert.That(_command.Cooldown, Is.EqualTo(TimeSpan.FromSeconds(15)));
    }
}
