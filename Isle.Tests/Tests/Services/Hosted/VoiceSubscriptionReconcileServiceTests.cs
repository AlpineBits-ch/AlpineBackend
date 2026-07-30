using Isle.Api.Services.Hosted;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="VoiceSubscriptionReconcileService"/> has real, meaty inline logic (grace/cooldown for
/// forgotten-track republish, once-per-process pair pushing) with no separate pure-logic class to
/// delegate to, and neither <see cref="VoiceCluster"/> nor <see cref="VoiceTrackRegistry"/> need any I/O
/// to construct — so <c>ReconcileAsync</c> and <c>OrderRepublishForForgottenTracks</c> (both `internal`
/// for this) are driven directly against real instances of both, with only <see cref="ISfuClient"/>
/// substituted.
/// </summary>
[TestFixture]
public class VoiceSubscriptionReconcileServiceTests
{
    private VoiceCluster _cluster = null!;
    private VoiceTrackRegistry _tracks = null!;
    private ISfuClient _sfu = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private VoiceSubscriptionReconcileService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cluster = new VoiceCluster(new VoiceGridConfig());
        _tracks = new VoiceTrackRegistry();
        _sfu = Substitute.For<ISfuClient>();

        var services = new ServiceCollection();
        services.AddSingleton(_sfu);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _service = new VoiceSubscriptionReconcileService(_cluster, _tracks, _scopeFactory, NullLogger<VoiceSubscriptionReconcileService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    private void PutTogether(string a, string b)
    {
        _cluster.MovePlayer(a, 0, 0, 0);
        _cluster.MovePlayer(b, 10, 10, 0); // well within the same 3000-unit cell as `a`
    }

    [Test]
    public async Task ReconcileAsync_NewlyAudiblePairWithPublishedTracks_SubscribesMutualAndSeedsBothPositions()
    {
        PutTogether("p1", "p2");
        _tracks.Publish("p1", "cf-session-1", "track-1");
        _tracks.Publish("p2", "cf-session-2", "track-2");

        await _service.ReconcileAsync();

        await _sfu.Received(1).SubscribeMutual("p1", "p2");
        await _sfu.Received(1).SendPeerPosition("p2", "p1", Arg.Is<float>(0), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
        await _sfu.Received(1).SendPeerPosition("p1", "p2", Arg.Is<float>(10), Arg.Is<float>(10), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task ReconcileAsync_PairAlreadyConfirmedThisProcess_IsNotRePushedOnASecondTick()
    {
        PutTogether("p1", "p2");
        _tracks.Publish("p1", "cf-session-1", "track-1");
        _tracks.Publish("p2", "cf-session-2", "track-2");

        await _service.ReconcileAsync();
        _sfu.ClearReceivedCalls();
        await _service.ReconcileAsync(); // still audible, already pushed once

        await _sfu.DidNotReceive().SubscribeMutual(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ReconcileAsync_PairDropsOutOfRangeThenReturns_IsTreatedAsNewAgain()
    {
        PutTogether("p1", "p2");
        _tracks.Publish("p1", "cf-session-1", "track-1");
        _tracks.Publish("p2", "cf-session-2", "track-2");
        await _service.ReconcileAsync();

        _cluster.MovePlayer("p2", 900_000, 900_000, 0); // far outside the grid's configured bounds/cell
        await _service.ReconcileAsync();

        PutTogether("p1", "p2"); // back together
        _sfu.ClearReceivedCalls();
        await _service.ReconcileAsync();

        await _sfu.Received(1).SubscribeMutual("p1", "p2");
    }

    [Test]
    public async Task ReconcileAsync_NoOneInTheGrid_DoesNothing()
    {
        Assert.DoesNotThrowAsync(() => _service.ReconcileAsync());

        await _sfu.DidNotReceiveWithAnyArgs().SubscribeMutual(default!, default!);
    }

    [Test]
    public async Task OrderRepublishForForgottenTracks_TracklessFirstTick_GetsGraceAndIsNotOrderedYet()
    {
        _cluster.MovePlayer("p1", 0, 0, 0); // in the grid, no published track

        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu);

        await _sfu.DidNotReceive().RequestRepublish(Arg.Any<string>());
    }

    [Test]
    public async Task OrderRepublishForForgottenTracks_TracklessTwoConsecutiveTicks_OrdersARepublish()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);

        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // tick 1: grace
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // tick 2: ordered

        await _sfu.Received(1).RequestRepublish("p1");
    }

    [Test]
    public async Task OrderRepublishForForgottenTracks_WithinCooldown_DoesNotReorderOnEveryTick()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // grace
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // ordered
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // still trackless, within cooldown

        await _sfu.Received(1).RequestRepublish("p1");
    }

    [Test]
    public async Task OrderRepublishForForgottenTracks_PlayerPublishesAfterBeingOrdered_ForgetsTheCooldownState()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // grace
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // ordered

        _tracks.Publish("p1", "cf-session-1", "track-1"); // client republished
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // no longer trackless

        _tracks.Remove("p1"); // track lost again
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // grace again (fresh)
        await _service.OrderRepublishForForgottenTracks(_cluster.GetPlayers(), _sfu); // ordered again

        await _sfu.Received(2).RequestRepublish("p1");
    }

    [Test]
    public async Task RunStartupForceRepublishAsync_PreCancelledToken_ReturnsEarlyWithoutOrderingAnything()
    {
        _cluster.MovePlayer("p1", 0, 0, 0); // trackless, would otherwise be a candidate

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.DoesNotThrowAsync(() => _service.RunStartupForceRepublishAsync(cts.Token));
        await _sfu.DidNotReceive().RequestRepublish(Arg.Any<string>());
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_CompletesWithoutException()
    {
        // Interval is a real 5s and StartupForceRepublishDelay a real 1 minute, so a quick start/stop
        // only ever exercises the Task.Delay cancellation paths of both the tick loop and the
        // fire-and-forget startup sweep (covered directly above).
        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => _service.StopAsync(CancellationToken.None));
    }
}
