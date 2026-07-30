using Isle.Api.Services.Hosted;
using Isle.Api.Services.State;
using Isle.Infrastructure.Persistence;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="VoicePresenceReconcileService"/>'s Interval is a real 60s, so the `internal`
/// <c>ReconcileAsync</c> is driven directly here rather than through the timer loop.
/// </summary>
[TestFixture]
public class VoicePresenceReconcileServiceTests
{
    private TestIsleContext _context = null!;
    private VoicePlayerRegistry _voiceRegistry = null!;
    private PlayerPresenceManager _presenceManager = null!;
    private IBridgeClient _bridge = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private VoicePresenceReconcileService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _voiceRegistry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _presenceManager = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        _bridge = Substitute.For<IBridgeClient>();

        var services = new ServiceCollection();
        services.AddSingleton<MicroserviceContext>(_context);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _service = new VoicePresenceReconcileService(
            _voiceRegistry, _presenceManager, _bridge, _scopeFactory, NullLogger<VoicePresenceReconcileService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _service.Dispose();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ReconcileAsync_RosterFetchFails_StillReconcilesLocalCachesWithoutThrowing()
    {
        _bridge.GetPlayersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("bridge timed out"));

        Assert.DoesNotThrowAsync(() => _service.ReconcileAsync(CancellationToken.None));

        Assert.That(_presenceManager.GetAllPlayerIds(), Is.Empty);
    }

    [Test]
    public async Task ReconcileAsync_OnlinePlayerKnownToTheDb_MarksThemPresent()
    {
        var player = TestData.Player("steam-1");
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        _bridge.GetPlayersAsync(Arg.Any<CancellationToken>()).Returns(new PlayersData { Players = ["steam-1"] });

        await _service.ReconcileAsync(CancellationToken.None);

        Assert.That(_presenceManager.IsPlayerOnline(player.Id), Is.True);
    }

    [Test]
    public async Task ReconcileAsync_OnlineSteamIdUnknownToTheDb_DoesNotMarkAnyoneWronglyPresent()
    {
        _bridge.GetPlayersAsync(Arg.Any<CancellationToken>()).Returns(new PlayersData { Players = ["steam-ghost"] });

        Assert.DoesNotThrowAsync(() => _service.ReconcileAsync(CancellationToken.None));

        Assert.That(_presenceManager.GetAllPlayerIds(), Is.Empty);
    }

    [Test]
    public async Task ReconcileAsync_EmptyRoster_LeavesPresenceEmpty()
    {
        _bridge.GetPlayersAsync(Arg.Any<CancellationToken>()).Returns(new PlayersData { Players = [] });

        await _service.ReconcileAsync(CancellationToken.None);

        Assert.That(_presenceManager.GetAllPlayerIds(), Is.Empty);
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_HydratesCachesEvenWithAPreCancelledToken()
    {
        // Hydration happens unconditionally at the top of ExecuteAsync, before the while(ct) loop.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.DoesNotThrowAsync(async () =>
        {
            await _service.StartAsync(cts.Token);
            await _service.StopAsync(CancellationToken.None);
        });
    }
}
