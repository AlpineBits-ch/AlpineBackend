using Echo.Realtime.LiveKit;
using Isle.Api.Services.Hosted;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Infrastructure.Sfu;
using Isle.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="VoiceSubscriptionReconcileService"/> has real, meaty inline logic - once-per-process
/// pair pushing, and a published-track map refreshed from the SFU - with no separate pure-logic
/// class to delegate to, and neither <see cref="VoiceCluster"/> nor <see
/// cref="VoiceTrackRegistry"/> need any I/O to construct.
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

        // An unconfigured fleet, which is what makes the poll a no-op: these tests are about the
        // pair-pushing half, and a room that does not exist has no participants to refresh from.
        var options = new LiveKitOptions();
        var registry = new LiveKitRoomRegistry(options, NullLogger<LiveKitRoomRegistry>.Instance);
        var client = new LiveKitRoomClient(
            new FakeHttpClientFactory(new QueuedHttpMessageHandler()), options,
            NullLogger<LiveKitRoomClient>.Instance);
        var room = new IsleVoiceRoom(options, registry, client, NullLogger<IsleVoiceRoom>.Instance);

        _service = new VoiceSubscriptionReconcileService(
            _cluster, _tracks, room, client, _scopeFactory,
            NullLogger<VoiceSubscriptionReconcileService>.Instance);
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
        _tracks.Publish("p1", "p1", "TR_sid-1", "track-1");
        _tracks.Publish("p2", "p2", "TR_sid-2", "track-2");

        await _service.ReconcileAsync();

        await _sfu.Received(1).SubscribeMutual("p1", "p2");
        await _sfu.Received(1).SendPeerPosition("p2", "p1", Arg.Is<float>(0), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
        await _sfu.Received(1).SendPeerPosition("p1", "p2", Arg.Is<float>(10), Arg.Is<float>(10), Arg.Is<float>(0), Arg.Is<float>(0), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task ReconcileAsync_PairAlreadyConfirmedThisProcess_IsNotRePushedOnASecondTick()
    {
        PutTogether("p1", "p2");
        _tracks.Publish("p1", "p1", "TR_sid-1", "track-1");
        _tracks.Publish("p2", "p2", "TR_sid-2", "track-2");

        await _service.ReconcileAsync();
        _sfu.ClearReceivedCalls();
        await _service.ReconcileAsync(); // still audible, already pushed once

        await _sfu.DidNotReceive().SubscribeMutual(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ReconcileAsync_PairDropsOutOfRangeThenReturns_IsTreatedAsNewAgain()
    {
        PutTogether("p1", "p2");
        _tracks.Publish("p1", "p1", "TR_sid-1", "track-1");
        _tracks.Publish("p2", "p2", "TR_sid-2", "track-2");
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

    /// <summary>
    /// The restart case, which used to need a client to be ordered to republish and now needs
    /// nothing: the SFU's answer is the map, so a process that starts with an empty one converges by
    /// asking.
    /// </summary>
    [Test]
    public void Sync_ReplacesTheMapWholesale()
    {
        _tracks.Publish("p1", "p1", "TR_old", "track-1");
        _tracks.Publish("p2", "p2", "TR_p2", "track-2");

        // What ListParticipants would report: p1 republished under a new sid, p2 has stopped.
        _tracks.Sync([("p1", new VoiceTrackRegistry.PublishedTrack("p1", "TR_new", "track-1"))]);

        Assert.Multiple(() =>
        {
            Assert.That(_tracks.TryGet("p1", out var p1), Is.True);
            Assert.That(p1.TrackSid, Is.EqualTo("TR_new"),
                "a merge would keep the sid of a publication that no longer exists");
            Assert.That(_tracks.TryGet("p2", out _), Is.False,
                "the SFU is authoritative about who is publishing, so an absence is an answer");
        });
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_CompletesWithoutException()
    {
        // Interval is a real 5s, so a quick start/stop only ever exercises the Task.Delay
        // cancellation path of the tick loop.
        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => _service.StopAsync(CancellationToken.None));
    }
}
