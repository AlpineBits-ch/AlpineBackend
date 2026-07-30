using Isle.Api.Services.Hosted;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.World;
using Isle.Infrastructure.Persistence;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="KingOfTheHillDirectorService"/> waits <c>StartupDelay</c> (40s) before its first tick
/// and then delays <c>Interval</c> (30s) between ticks, so a short lifecycle test can only ever
/// exercise the startup-cancellation path.
/// </summary>
[TestFixture]
public class KingOfTheHillDirectorServiceTests
{
    private TestIsleContext _context = null!;
    private WorldRosterCache _roster = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private FakeRedisStore _redisStore = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _roster = new WorldRosterCache();
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(out _redisStore), NullLogger<KingOfTheHillMatchStateStore>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private KingOfTheHillDirectorService BuildService(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, _roster, _stateStore, NullLogger<KingOfTheHillDirectorService>.Instance);

    private IServiceScopeFactory RealScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MicroserviceContext>(_context);
        services.AddSingleton(_roster);
        services.AddScoped<KingOfTheHillDirector>();
        services.AddSingleton(NullLogger<KingOfTheHillDirector>.Instance);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Test]
    public async Task SafeTickAsync_StaleRosterAndNoMarker_NeverCreatesAScope()
    {
        // WorldRosterCache is never populated -> IsStale is always true.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = BuildService(scopeFactory);

        await service.SafeTickAsync(CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task SafeTickAsync_StaleRosterWithLiveMarker_StillSkipsWithoutCreatingAScope()
    {
        await _stateStore.WriteAsync(new KothMatchState("def_1", "instance_1", DateTime.UtcNow, []));
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = BuildService(scopeFactory);

        await service.SafeTickAsync(CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task SafeTickAsync_FreshRosterWithMarkerForMissingDefinition_ClearsTheMarker()
    {
        _roster.Replace([]); // fresh (just updated)
        await _stateStore.WriteAsync(new KothMatchState("def_missing", "instance_1", DateTime.UtcNow, []));
        var service = BuildService(RealScopeFactory());

        await service.SafeTickAsync(CancellationToken.None);

        Assert.That(await _stateStore.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task SafeTickAsync_FreshRosterNoMarkerNoEligibleDefinition_CompletesWithoutError()
    {
        _roster.Replace([]); // fresh, but empty roster and no seeded GameModeDefinition rows
        var service = BuildService(RealScopeFactory());

        Assert.DoesNotThrowAsync(() => service.SafeTickAsync(CancellationToken.None));
        Assert.That(await _stateStore.ReadAsync(), Is.Null, "no candidate should have been chosen, so nothing should have been written");
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_NeverCreatesAScopeDuringStartupDelay()
    {
        // StartupDelay is a real 40s, so a quick start/stop only ever exercises that delay's
        // cancellation path and never reaches SafeTickAsync (covered directly above).
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = BuildService(scopeFactory);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => service.StopAsync(CancellationToken.None));

        scopeFactory.DidNotReceive().CreateScope();
    }
}
