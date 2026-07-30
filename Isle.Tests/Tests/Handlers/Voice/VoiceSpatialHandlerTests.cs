using Isle.Api.Handlers.Voice;
using Isle.Contracts.Events.Voice;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using NSubstitute;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class VoiceSpatialHandlerTests
{
    private VoiceCluster _cluster = null!;
    private ISfuClient _sfu = null!;

    [SetUp]
    public void SetUp()
    {
        _cluster = new VoiceCluster(new VoiceGridConfig());
        _sfu = Substitute.For<ISfuClient>();
    }

    [Test]
    public async Task Handle_AlwaysSendsSelfPosition_EvenAlone()
    {
        var @event = new PlayerPositionUpdatedEvent("p1", 10, 20, 30, 45f, 1, 2, 3, 1000);

        await VoiceSpatialHandler.Handle(@event, _cluster, _sfu);

        await _sfu.Received(1).SendSelfPosition("p1", 10, 20, 30, 45f, 1, 2, 3, 1000);
    }

    [Test]
    public async Task Handle_NoAudiblePeers_DoesNotBroadcast()
    {
        var @event = new PlayerPositionUpdatedEvent("p1", 10, 20, 30);

        await VoiceSpatialHandler.Handle(@event, _cluster, _sfu);

        await _sfu.DidNotReceive().BroadcastPosition(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<long>());
    }

    [Test]
    public async Task Handle_WithAudiblePeer_BroadcastsToThatPeer()
    {
        _cluster.MovePlayer("p1", 0, 0, 0);
        _cluster.MovePlayer("p2", 100, 100, 0);
        var @event = new PlayerPositionUpdatedEvent("p1", 0, 0, 0);

        await VoiceSpatialHandler.Handle(@event, _cluster, _sfu);

        await _sfu.Received(1).BroadcastPosition("p1", Arg.Is<IReadOnlyList<string>>(c => c.Contains("p2")),
            0, 0, 0, 0f, 0f, 0f, 0f, 0);
    }
}
