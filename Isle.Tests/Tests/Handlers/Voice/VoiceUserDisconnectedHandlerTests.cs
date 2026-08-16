using Echo.Realtime;
using Isle.Api.Handlers.Voice;
using Isle.Api.Services.State;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class VoiceUserDisconnectedHandlerTests
{
    private VoicePlayerRegistry _registry = null!;
    private VoiceTrackRegistry _tracks = null!;
    private VoiceCluster _cluster = null!;
    private ISfuClient _sfu = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _tracks = new VoiceTrackRegistry();
        _cluster = new VoiceCluster(new VoiceGridConfig());
        _sfu = Substitute.For<ISfuClient>();
    }

    private Task HandleAsync(string userId) =>
        VoiceUserDisconnectedHandler.Handle(new UserDisconnected(userId), _registry, _tracks, _cluster, _sfu);

    [Test]
    public async Task Handle_NotAVoiceParticipant_NoOp()
    {
        await HandleAsync("player_ghost");

        await _sfu.DidNotReceive().UnsubscribePair(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_VoiceParticipantNotPublishing_NoOp()
    {
        await _registry.RegisterAsync("player_1", "steam_1");
        // No track published.

        await HandleAsync("player_1");

        await _sfu.DidNotReceive().UnsubscribePair(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_PublishingParticipantWithNoAudiblePeers_RemovesTrackButUnsubscribesNobody()
    {
        await _registry.RegisterAsync("player_1", "steam_1");
        _tracks.Publish("player_1", "player_1", "TR_sid", "track-1");
        _cluster.MovePlayer("player_1", 0, 0, 0);

        await HandleAsync("player_1");

        Assert.That(_tracks.TryGet("player_1", out _), Is.False, "the dead track must be dropped");
        await _sfu.DidNotReceive().UnsubscribePair(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_PublishingParticipantWithAudiblePeers_DropsTrackAndUnsubscribesEveryPeer()
    {
        await _registry.RegisterAsync("player_1", "steam_1");
        _tracks.Publish("player_1", "player_1", "TR_sid", "track-1");
        _cluster.MovePlayer("player_1", 0, 0, 0);
        _cluster.MovePlayer("player_2", 10, 10, 0);
        _cluster.MovePlayer("player_3", 20, 20, 0);

        await HandleAsync("player_1");

        Assert.That(_tracks.TryGet("player_1", out _), Is.False);
        await _sfu.Received(1).UnsubscribePair("player_1", "player_2");
        await _sfu.Received(1).UnsubscribePair("player_1", "player_3");
    }
}
