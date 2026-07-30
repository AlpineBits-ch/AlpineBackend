using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Interfaces;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class KothAdminCommandTests
{
    private TestIsleContext _context = null!;
    private WorldRosterCache _roster = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private KingOfTheHillDirector _director = null!;
    private KingOfTheHillSpawner _spawner = null!;
    private KingOfTheHillCompletionService _completion = null!;
    private KothAdminCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _roster = new WorldRosterCache();
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(), NullLogger<KingOfTheHillMatchStateStore>.Instance);
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);

        var bridge = BridgeTestFactory.CreateDefault();
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new KingOfTheHillAnnouncer(bridge, NullLogger<KingOfTheHillAnnouncer>.Instance, presence, _context);
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var bus = Substitute.For<IMessageBus>();
        var behavior = Substitute.For<IGameMode>();

        _director = new KingOfTheHillDirector(_context, _roster, NullLogger<KingOfTheHillDirector>.Instance);
        _spawner = new KingOfTheHillSpawner(_context, behavior, _roster, _stateStore, announcer, bus, NullLogger<KingOfTheHillSpawner>.Instance);
        _completion = new KingOfTheHillCompletionService(_context, _ledger, _stateStore, rewards, announcer, bus, NullLogger<KingOfTheHillCompletionService>.Instance);

        _command = new KothAdminCommand(_director, _spawner, _completion, _stateStore, _ledger);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private const string Usage = "Usage: !kothadmin start | end | status";

    [Test]
    public async Task ExecuteAsync_NoArguments_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Is.EqualTo(Usage));
    }

    [Test]
    public async Task ExecuteAsync_UnknownVerb_ReturnsUsage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bogus"] });

        Assert.That(result, Is.EqualTo(Usage));
    }

    [Test]
    public async Task Start_NoDefinitionConfigured_ReturnsNotEnabledMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["start"] });

        Assert.That(result, Does.Contain("not enabled, or is still on cooldown"));
    }

    [Test]
    public async Task Start_MatchAlreadyRunning_ReturnsAlreadyRunningMessage()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition());
        await _context.SaveChangesAsync();
        await _stateStore.WriteAsync(new KothMatchState("def_1", "instance_1", DateTime.UtcNow, []));

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["start"] });

        Assert.That(result, Does.Contain("already running"));
    }

    [Test]
    public async Task Start_DefinitionAvailable_SpawnsMatchAndWritesMarker()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition());
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["start"] });

        Assert.That(result, Does.Contain("Started King of the Hill (instance"));
        Assert.That(await _stateStore.ReadAsync(), Is.Not.Null);
    }

    [Test]
    public async Task Start_DefinitionOnCooldown_ReturnsNotEnabledMessage()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition(
            cooldown: TimeSpan.FromMinutes(20), lastRunAt: DateTime.UtcNow.AddMinutes(-1)));
        await _context.SaveChangesAsync();

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["start"] });

        Assert.That(result, Does.Contain("not enabled, or is still on cooldown"));
    }

    [Test]
    public async Task End_NoMatchRunning_ReturnsNotRunningMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end"] });

        Assert.That(result, Is.EqualTo("No King of the Hill match is running."));
    }

    [Test]
    public async Task End_MatchRunning_CancelsAndClearsMarker()
    {
        _context.GameModeDefinitions.Add(TestData.KothDefinition());
        await _context.SaveChangesAsync();
        await _stateStore.WriteAsync(new KothMatchState("def_1", "instance_1", DateTime.UtcNow, []));

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["end"] });

        Assert.That(result, Is.EqualTo("King of the Hill match cancelled."));
        Assert.That(await _stateStore.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task Status_NoMatchRunning_ReturnsNotRunningMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["status"] });

        Assert.That(result, Is.EqualTo("No King of the Hill match is running."));
    }

    [Test]
    public async Task Status_MatchRunningNoControlTicks_ReturnsNoTicksMessage()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", "instance_1", DateTime.UtcNow, []));

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["status"] });

        Assert.That(result, Does.Contain("instance_1: no control ticks credited yet."));
    }

    [Test]
    public async Task Status_MatchRunningWithStandings_ListsSteamIdsAndTicks()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", "instance_1", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance_1", ["steam_a"]);

        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["status"] });

        Assert.That(result, Does.Contain("instance_1:"));
        Assert.That(result, Does.Contain("steam_a=1"));
    }

    [Test]
    public void IsAdminOnly_IsTrue()
    {
        Assert.That(_command.IsAdminOnly, Is.True);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsFalse()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] }), Is.False);
    }

    [Test]
    public void CanRun_AdminContext_ReturnsTrue()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = true, Arguments = [] }), Is.True);
    }
}
